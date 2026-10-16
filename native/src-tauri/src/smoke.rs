//! Hidden off-screen verification (`--smoke-output <report.json>`): the web suite drives the real
//! commands in an invisible window, captures WebView2 renders and writes a JSON report.
use crate::state::AssetLoader;
use crate::{App, worker};
use std::path::PathBuf;
use tauri::Manager;

fn error(e: impl std::fmt::Display) -> String {
    e.to_string()
}

fn output(app: &tauri::State<'_, App>) -> Result<PathBuf, String> {
    app.smoke.clone().ok_or_else(|| "Smoke mode is disabled".into())
}

#[cfg(windows)]
async fn capture_hidden(app: &tauri::AppHandle, screenshot: PathBuf) -> Result<(), String> {
    let window = app.get_webview_window("main").ok_or("Missing window")?;
    let (tx, rx) = std::sync::mpsc::channel();
    window
        .with_webview(move |webview| {
            let failure = tx.clone();
            let result = (|| -> Result<(), String> {
                use std::os::windows::ffi::OsStrExt;
                use webview2_com::{CapturePreviewCompletedHandler, Microsoft::Web::WebView2::Win32::COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG};
                use windows::{Win32::UI::Shell::SHCreateStreamOnFileEx, core::PCWSTR};
                let filename: Vec<u16> = screenshot.as_os_str().encode_wide().chain(Some(0)).collect();
                // The path derives from the developer's smoke CLI argument, never from the web UI.
                let stream = unsafe { SHCreateStreamOnFileEx(PCWSTR(filename.as_ptr()), 0x1001, 0, true, None) }.map_err(error)?;
                let retained = stream.clone();
                let callback = CapturePreviewCompletedHandler::create(Box::new(move |result| {
                    drop(retained);
                    let _ = tx.send(result.map_err(error));
                    Ok(())
                }));
                unsafe { webview.controller().CoreWebView2().map_err(error)?.CapturePreview(COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG, &stream, &callback) }.map_err(error)?;
                Ok(())
            })();
            if let Err(err) = result {
                let _ = failure.send(Err(err));
            }
        })
        .map_err(error)?;
    worker(move || rx.recv_timeout(std::time::Duration::from_secs(15)).map_err(error)?).await
}

/// Captures the hidden window as `<report>.<name>.png` (name limited to safe characters).
#[tauri::command]
pub async fn smoke_capture(handle: tauri::AppHandle, app: tauri::State<'_, App>, name: String) -> Result<(), String> {
    let path = output(&app)?;
    if name.is_empty() || name.len() > 40 || !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '-') {
        return Err("Invalid capture name".into());
    }
    #[cfg(windows)]
    capture_hidden(&handle, path.with_extension(format!("{name}.png"))).await?;
    Ok(())
}

#[tauri::command]
pub async fn smoke_report(handle: tauri::AppHandle, app: tauri::State<'_, App>, report: serde_json::Value) -> Result<(), String> {
    let path = output(&app)?;
    std::fs::write(&path, serde_json::to_vec_pretty(&report).map_err(error)?).map_err(error)?;
    handle.exit(if report["passed"] == true { 0 } else { 1 });
    Ok(())
}

/// Opens a project fixture named by the `COURT_CREATOR_SMOKE_PROJECT` environment variable.
#[tauri::command]
pub async fn smoke_open_fixture(app: tauri::State<'_, App>) -> Result<Option<serde_json::Value>, String> {
    output(&app)?;
    let Some(path) = std::env::var_os("COURT_CREATOR_SMOKE_PROJECT").map(PathBuf::from) else { return Ok(None) };
    let session = app.session.clone();
    worker(move || {
        let mut value = court_core::project::read(&path).map_err(error)?;
        value["_projectPath"] = serde_json::Value::String(path.to_string_lossy().into_owned());
        let mut session = session.lock().map_err(error)?;
        let crate::state::Session { studio, cache, .. } = &mut *session;
        let studio = studio.as_mut().ok_or("Studio not ready")?;
        let report = studio.restore(&value, true, &mut AssetLoader { cache }).map_err(error)?;
        Ok(Some(serde_json::json!({"status": report.status(), "doc": crate::commands::doc_view(studio)})))
    })
    .await
}

/// Exports the current court next to the report (PNG or IFF) without a dialog.
#[tauri::command]
pub async fn smoke_export(app: tauri::State<'_, App>, iff: bool) -> Result<String, String> {
    let path = output(&app)?.with_extension(if iff { "export.iff" } else { "export.png" });
    let session = app.session.clone();
    worker(move || {
        let (project, geometry) = {
            let mut session = session.lock().map_err(error)?;
            let studio = session.studio().map_err(error)?;
            (studio.create_project(), studio.library.geometry.clone())
        };
        let request = court_core::render::RenderRequest::from_project(&project, &|p: &str| court_core::project::absolute(p, &court_core::files::data_root())).map_err(error)?;
        let started = std::time::Instant::now();
        let progress = court_core::export::Progress { report: &|_| {}, cancelled: &|| false };
        if iff { court_core::export::export_iff(&geometry, &request, &path, None, &progress) } else { court_core::export::export_png(&geometry, &request, &path, &progress) }.map_err(error)?;
        Ok(format!("{} in {} ms", path.display(), started.elapsed().as_millis()))
    })
    .await
}

/// Portable Save then Open of the current court in a folder next to the report (no dialogs).
#[tauri::command]
pub async fn smoke_save_reopen(app: tauri::State<'_, App>) -> Result<serde_json::Value, String> {
    let folder = output(&app)?.with_extension("portable");
    let session = app.session.clone();
    worker(move || {
        let _ = std::fs::remove_dir_all(&folder);
        std::fs::create_dir_all(&folder).map_err(error)?;
        let path = folder.join("Smoke Court.court.json");
        let snapshot = session.lock().map_err(error)?.studio().map_err(error)?.create_project();
        court_core::project::save_portable(&path, &snapshot, &court_core::files::data_root()).map_err(error)?;
        let mut value = court_core::project::read(&path).map_err(error)?;
        let saved = value.clone();
        value["_projectPath"] = serde_json::Value::String(path.to_string_lossy().into_owned());
        let mut session = session.lock().map_err(error)?;
        let crate::state::Session { studio, cache, .. } = &mut *session;
        let studio = studio.as_mut().ok_or("Studio not ready")?;
        let report = studio.restore(&value, true, &mut AssetLoader { cache }).map_err(error)?;
        Ok(serde_json::json!({"status": report.status(), "saved": saved, "doc": crate::commands::doc_view(studio)}))
    })
    .await
}

/// Exits nonzero with a report if the hidden suite stalls.
pub fn watchdog(handle: tauri::AppHandle, path: PathBuf) {
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_secs(420));
        let _ = std::fs::write(&path, br#"{"passed":false,"error":"Hidden smoke suite timed out after 420 seconds."}"#);
        handle.exit(3);
    });
}
