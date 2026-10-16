//! Process state: the open court, decoded artwork caches, preferences and the recovery writer.
use court_core::document::{Assets, Studio};
use court_core::error::{Error, Result};
use court_core::raster::{self, Rgba};
use court_core::{files, project, pyjson};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};

/// Hardwood preview resolution relative to the 8192 x 4096 document (the WPF studio decoded floors at 2048 px).
pub const PREVIEW_SCALE: f64 = 0.375;
pub const LOGO_PREVIEW_SIDE: u32 = 2048;

pub struct DecodedFloor {
    pub revision: String,
    pub image: Arc<Rgba>,
}

#[derive(Default)]
pub struct Cache {
    floors: HashMap<(PathBuf, bool), Arc<DecodedFloor>>,
    floor_order: Vec<(PathBuf, bool)>,
    fitted: HashMap<(String, bool, u32, u32), Arc<Rgba>>,
    pub logos: HashMap<String, Arc<Rgba>>,
}

impl Cache {
    pub fn decoded_floor(&mut self, path: &Path, game_data: bool) -> Result<Arc<DecodedFloor>> {
        let key = (files::normalized(path), game_data);
        if let Some(found) = self.floors.get(&key) {
            // Same path may have been replaced on disk; a cheap revision check keeps previews honest.
            if files::file_revision(path).ok().as_deref() == Some(found.revision.as_str()) {
                return Ok(found.clone());
            }
        }
        let bytes = files::read_bounded(path, files::MAX_ASSET_BYTES).map_err(|e| Error::msg(format!("The hardwood image could not be read: {e}")))?;
        let revision = files::sha256_hex(&bytes);
        let mut image = raster::decode(&bytes, path.extension().and_then(|e| e.to_str()))?;
        if game_data {
            image.set_alpha(255);
        }
        let decoded = Arc::new(DecodedFloor { revision, image: Arc::new(image) });
        self.floors.insert(key.clone(), decoded.clone());
        self.floor_order.retain(|k| *k != key);
        self.floor_order.push(key);
        while self.floor_order.len() > 4 {
            let old = self.floor_order.remove(0);
            self.floors.remove(&old);
        }
        Ok(decoded)
    }

    pub fn fitted_cached(&self, revision: &str, game_data: bool, width: u32, height: u32) -> Option<Arc<Rgba>> {
        self.fitted.get(&(revision.to_string(), game_data, width, height)).cloned()
    }

    /// Keeps fitted (Lanczos, centered crop) preview hardwoods, before color/placement settings.
    pub fn store_fitted(&mut self, revision: &str, game_data: bool, width: u32, height: u32, image: Arc<Rgba>) {
        if self.fitted.len() > 6 {
            self.fitted.clear();
        }
        self.fitted.insert((revision.to_string(), game_data, width, height), image);
    }

    pub fn logo_preview(&self, revision: &str) -> Option<Arc<Rgba>> {
        self.logos.get(revision).cloned()
    }
}

/// Asset access for document restores and selections. External logos are copied into
/// app-owned storage before use, as the WPF studio did; originals are never modified.
pub struct AssetLoader<'a> {
    pub cache: &'a mut Cache,
}

pub fn logos_directory() -> PathBuf {
    files::data_root().join("logos")
}

/// Copies artwork into `logos/<sha256><ext>` unless it already lives there.
pub fn own_logo(path: &Path, bytes: &[u8], revision: &str) -> Result<PathBuf> {
    let directory = logos_directory();
    if files::normalized(path).starts_with(files::normalized(&directory)) {
        return Ok(path.to_path_buf());
    }
    let mut extension = path.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_else(|| "png".into());
    if extension.len() > 15 || !extension.chars().all(|c| c.is_ascii_alphanumeric()) {
        extension = "image".into();
    }
    let owned = directory.join(format!("{revision}.{extension}"));
    if owned.is_file() && files::file_revision(&owned).ok().as_deref() == Some(revision) {
        return Ok(owned);
    }
    files::write_atomic(&owned, bytes, &[path])?;
    Ok(owned)
}

impl Assets for AssetLoader<'_> {
    fn floor(&mut self, path: &Path, game_data: bool) -> Result<String> {
        Ok(self.cache.decoded_floor(path, game_data)?.revision.clone())
    }
    fn logo(&mut self, path: &Path, game_data: bool) -> Result<(PathBuf, String, u32, u32)> {
        let bytes = files::read_bounded(path, files::MAX_ASSET_BYTES)?;
        let revision = files::sha256_hex(&bytes);
        let owned = own_logo(path, &bytes, &revision)?;
        if let Some(found) = self.cache.logos.get(&revision) {
            let dims = logo_dimensions(&bytes, path).unwrap_or((found.width, found.height));
            return Ok((owned, revision, dims.0, dims.1));
        }
        let mut image = raster::decode(&bytes, path.extension().and_then(|e| e.to_str()))?;
        if game_data {
            image.set_alpha(255);
        }
        let size = (image.width, image.height);
        self.cache.logos.insert(revision.clone(), Arc::new(raster::preview(&image, LOGO_PREVIEW_SIDE)));
        Ok((owned, revision, size.0, size.1))
    }
}

fn logo_dimensions(bytes: &[u8], path: &Path) -> Option<(u32, u32)> {
    raster::dimensions(bytes, path.extension().and_then(|e| e.to_str())).ok()
}

// --- Preferences (favorites, recent courts, theme, primary color) ---

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Preferences {
    pub favorites: Vec<String>,
    pub recent: Vec<String>,
    pub dark: bool,
    pub primary_color: String,
}

impl Default for Preferences {
    fn default() -> Self {
        Self { favorites: Vec::new(), recent: Vec::new(), dark: false, primary_color: "#19583F".into() }
    }
}

impl Preferences {
    pub fn path() -> PathBuf {
        files::data_root().join("preferences.json")
    }
    /// Port of `StudioPreferences.FromDocument` (strict keys, bounded lists, valid color).
    pub fn from_document(value: &Value) -> Result<Self> {
        let map = value.as_object().ok_or_else(|| Error::msg("Preferences must be an object."))?;
        if map.keys().any(|k| !matches!(k.as_str(), "favorites" | "recent" | "dark" | "primaryColor")) {
            return Err(Error::msg("Preferences contain unrecognized settings."));
        }
        let ids = |key: &str, limit: usize| -> Result<Vec<String>> {
            let Some(items) = map.get(key) else { return Ok(Vec::new()) };
            let items = items.as_array().filter(|a| a.len() <= 10000).ok_or_else(|| Error::msg(format!("Invalid {key} preferences.")))?;
            let mut out: Vec<String> = Vec::new();
            for item in items {
                let id = item.as_str().filter(|s| !s.trim().is_empty() && s.len() <= 1024 && !s.contains('\0')).ok_or_else(|| Error::msg(format!("Invalid court ID in {key}.")))?;
                if !out.iter().any(|o| o == id) {
                    out.push(id.to_string());
                }
            }
            out.truncate(limit);
            Ok(out)
        };
        let dark = match map.get("dark") {
            None => false,
            Some(Value::Bool(flag)) => *flag,
            Some(_) => return Err(Error::msg("Invalid theme preference.")),
        };
        let primary = match map.get("primaryColor") {
            None => "#19583F".into(),
            Some(value) => value.as_str().and_then(court_core::render::normalize_hex).ok_or_else(|| Error::msg("Invalid primary color preference."))?,
        };
        Ok(Self { favorites: ids("favorites", 10000)?, recent: ids("recent", 20)?, dark, primary_color: primary })
    }
    pub fn to_document(&self) -> Value {
        json!({"favorites": self.favorites, "recent": self.recent, "dark": self.dark, "primaryColor": self.primary_color})
    }
    /// Loads the preview's own preferences, seeding once (read-only) from the existing app's file.
    pub fn load() -> (Self, Option<String>) {
        let read = |path: &Path| -> Result<Self> { Self::from_document(&pyjson::read_file(path, 1024 * 1024, 8)?) };
        let own = Self::path();
        if own.exists() {
            return match read(&own) {
                Ok(prefs) => (prefs, None),
                Err(error) => (Self::default(), Some(format!("Preferences ignored: {error}"))),
            };
        }
        let legacy = files::legacy_settings_root().join("preferences.json");
        (read(&legacy).unwrap_or_default(), None)
    }
}

/// Coalescing background writer: at most one active write and the latest pending snapshot.
pub struct Writer {
    pending: Mutex<Option<(u64, Value)>>,
    signal: Condvar,
    pub latest: AtomicU64,
    pub completed: AtomicU64,
    pub error: Mutex<Option<String>>,
    stop: AtomicBool,
}

impl Writer {
    pub fn start(name: &'static str, write: fn(&Value) -> Result<()>) -> Arc<Self> {
        let writer = Arc::new(Self { pending: Mutex::new(None), signal: Condvar::new(), latest: AtomicU64::new(0), completed: AtomicU64::new(0), error: Mutex::new(None), stop: AtomicBool::new(false) });
        let worker = writer.clone();
        std::thread::Builder::new()
            .name(name.into())
            .spawn(move || loop {
                let next = {
                    let mut pending = worker.pending.lock().unwrap();
                    while pending.is_none() && !worker.stop.load(Ordering::Acquire) {
                        pending = worker.signal.wait(pending).unwrap();
                    }
                    pending.take()
                };
                let Some((revision, value)) = next else { return };
                let result = write(&value);
                *worker.error.lock().unwrap() = result.err().map(|e| e.to_string());
                worker.completed.store(revision, Ordering::Release);
                worker.signal.notify_all();
            })
            .expect("writer thread");
        writer
    }

    pub fn queue(&self, value: Value) -> u64 {
        let revision = self.latest.fetch_add(1, Ordering::AcqRel) + 1;
        *self.pending.lock().unwrap() = Some((revision, value));
        self.signal.notify_all();
        revision
    }

    /// Waits until the given revision has been written (or `timeout` passes).
    pub fn flush(&self, revision: u64, timeout: std::time::Duration) -> Result<()> {
        let deadline = std::time::Instant::now() + timeout;
        let mut pending = self.pending.lock().unwrap();
        while self.completed.load(Ordering::Acquire) < revision {
            let now = std::time::Instant::now();
            if now >= deadline {
                return Err(Error::msg("Saving recovery took too long."));
            }
            pending = self.signal.wait_timeout(pending, deadline - now).unwrap().0;
        }
        drop(pending);
        match self.error.lock().unwrap().clone() {
            Some(error) => Err(Error::msg(error)),
            None => Ok(()),
        }
    }
}

pub fn write_recovery(value: &Value) -> Result<()> {
    project::write_recovery(&project::recovery_path(), value)
}

pub fn write_preferences(value: &Value) -> Result<()> {
    let prefs = Preferences::from_document(value)?;
    let path = Preferences::path();
    if path.exists() && Preferences::from_document(&pyjson::read_file(&path, 1024 * 1024, 8)?).is_err() {
        return Err(Error::msg(format!("The existing preferences file is damaged or unrecognized; preserve and repair or rename it before saving settings: {}", path.display())));
    }
    let data = serde_json::to_vec(&prefs.to_document())?;
    files::write_atomic(&path, &data, &[])
}

/// Everything behind the app's single state lock.
pub struct Session {
    pub studio: Option<Studio>,
    pub cache: Cache,
    pub prefs: Preferences,
    pub geometry_regions: HashMap<Option<String>, Arc<court_core::geometry::Regions>>,
    pub importers: HashMap<u64, crate::commands::Importer>,
    pub next_importer: u64,
    pub library_name: String,
    pub library_complete: bool,
    /// Set while a save or export runs; edits are rejected until it clears.
    pub busy: Option<String>,
    /// Inspected source court for the conversion section.
    pub import: Option<court_core::iff::Inspection>,
}

impl Session {
    pub fn studio(&mut self) -> Result<&mut Studio> {
        self.studio.as_mut().ok_or_else(|| Error::msg("The court workspace is still loading."))
    }
    pub fn regions(&mut self, boundary: Option<&str>) -> Option<Arc<court_core::geometry::Regions>> {
        let key = boundary.map(str::to_string);
        if let Some(found) = self.geometry_regions.get(&key) {
            return Some(found.clone());
        }
        let geometry = self.studio.as_ref()?.library.geometry.clone();
        let regions = Arc::new(court_core::geometry::regions(&geometry, boundary));
        self.geometry_regions.insert(key, regions.clone());
        Some(regions)
    }
}
