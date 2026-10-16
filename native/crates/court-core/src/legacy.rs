//! Read-only discovery of existing Court Creator installations whose validated caches may be reused.
//!
//! Nothing here writes to those folders: the Rust preview copies or references validated files and
//! keeps its own generated data under [`crate::files::data_root`].
use std::path::PathBuf;

/// Existing application roots, newest first (portable extractions, installed copy, explicit override).
pub fn roots() -> Vec<PathBuf> {
    let mut found: Vec<(std::time::SystemTime, PathBuf)> = Vec::new();
    if let Ok(custom) = std::env::var("COURT_CREATOR_LEGACY_ROOT") {
        if !custom.trim().is_empty() {
            found.push((std::time::SystemTime::now(), PathBuf::from(custom)));
        }
    }
    if let Some(local) = std::env::var_os("LOCALAPPDATA").map(PathBuf::from) {
        if let Ok(entries) = std::fs::read_dir(local.join("2K Court Creator Portable")) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.join("portable-manifest.json").is_file() || path.join("data").is_dir() {
                    let modified = std::fs::metadata(path.join("data").join("generated")).and_then(|m| m.modified()).or_else(|_| entry.metadata().and_then(|m| m.modified())).unwrap_or(std::time::UNIX_EPOCH);
                    found.push((modified, path));
                }
            }
        }
        let installed = local.join("Programs").join("NBA 2K Court Creator");
        for candidate in [installed.join("Portable"), installed] {
            if candidate.join("data").is_dir() || candidate.join("assets").is_dir() {
                let modified = std::fs::metadata(&candidate).and_then(|m| m.modified()).unwrap_or(std::time::UNIX_EPOCH);
                found.push((modified, candidate));
            }
        }
    }
    found.sort_by(|a, b| b.0.cmp(&a.0));
    let mut unique = Vec::new();
    for (_, path) in found {
        if !unique.contains(&path) {
            unique.push(path);
        }
    }
    unique
}

/// Existing game-folder choices saved by earlier versions (read only).
pub fn saved_game_roots() -> Vec<PathBuf> {
    roots()
        .into_iter()
        .filter_map(|root| crate::pyjson::read_file(&root.join("data").join("game_installation.json"), 1024 * 1024, 32).ok())
        .filter_map(|value| value.get("nba2k27Root").and_then(|v| v.as_str()).map(PathBuf::from))
        .collect()
}
