//! `.court.json` projects: validation, bounded reads, atomic writes with backups and portable
//! `<name>.assets` bundling (ports of StudioProjectValidation/Store/Assets/PortableAssets).
use crate::error::{Error, Result};
use crate::files::{self, MAX_ASSET_BYTES};
use crate::pyjson;
use serde_json::{Map, Value};
use std::collections::HashMap;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

pub const MAX_PROJECT_BYTES: usize = 16 * 1024 * 1024;

fn invalid(key: &str, message: &str) -> Error {
    Error::msg(format!("Invalid court project: {key} {message}."))
}

fn text<'a>(value: Option<&'a Value>, key: &str) -> Result<Option<&'a str>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(text)) if text.encode_utf16().count() <= 32768 && !text.contains('\0') => Ok(Some(text)),
        Some(_) => Err(invalid(key, "must be valid text")),
    }
}

fn boolean(value: Option<&Value>, key: &str) -> Result<()> {
    match value {
        None | Some(Value::Null) | Some(Value::Bool(_)) => Ok(()),
        Some(_) => Err(invalid(key, "must be true or false")),
    }
}

fn color(value: Option<&Value>, key: &str) -> Result<()> {
    if let Some(text) = text(value, key)? {
        if crate::render::normalize_hex(text).is_none() {
            return Err(invalid(key, "must be a valid hex color"));
        }
    }
    Ok(())
}

fn revision(value: Option<&Value>, key: &str) -> Result<()> {
    if let Some(text) = text(value, key)? {
        if !files::is_revision(text) {
            return Err(invalid(key, "must be a SHA-256 revision"));
        }
    }
    Ok(())
}

fn number(value: Option<&Value>, key: &str, required: bool) -> Result<f64> {
    match value {
        None | Some(Value::Null) if !required => Ok(0.0),
        Some(Value::Number(n)) => n.as_f64().filter(|v| v.is_finite()).ok_or_else(|| invalid(key, "must be a finite number")),
        _ => Err(invalid(key, "must be a finite number")),
    }
}

fn object<'a>(value: Option<&'a Value>, key: &str) -> Result<&'a Map<String, Value>> {
    value.and_then(Value::as_object).ok_or_else(|| invalid(key, "must be an object"))
}

fn map<'a>(value: Option<&'a Value>, key: &str) -> Result<Option<&'a Map<String, Value>>> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Object(map)) => Ok(Some(map)),
        Some(_) => Err(invalid(key, "must be an object")),
    }
}

fn array<'a>(value: Option<&'a Value>, key: &str) -> Result<&'a [Value]> {
    match value {
        None | Some(Value::Null) => Ok(&[]),
        Some(Value::Array(items)) => Ok(items),
        Some(_) => Err(invalid(key, "must be an array")),
    }
}

fn artwork(item: &Map<String, Value>) -> Result<()> {
    if let Some(mode) = text(item.get("artworkAlphaMode"), "artworkAlphaMode")? {
        if mode != "GameData" && mode != "Transparency" {
            return Err(invalid("artworkAlphaMode", "is not supported"));
        }
    }
    for key in ["artworkProject", "artworkDds"] {
        let path = text(item.get(&format!("{key}Path")), &format!("{key}Path"))?;
        revision(item.get(&format!("{key}Revision")), &format!("{key}Revision"))?;
        if let Some(path) = path {
            if path.is_empty() || item.get(&format!("{key}Revision")).is_none_or(Value::is_null) {
                return Err(invalid(key, "must have a path and revision"));
            }
        }
    }
    Ok(())
}

fn css_color(value: &str) -> bool {
    let hex = value.trim().trim_start_matches('#');
    if value.trim().starts_with('#') {
        return matches!(hex.len(), 3 | 4 | 6 | 8) && hex.chars().all(|c| c.is_ascii_hexdigit());
    }
    !value.is_empty() && value.chars().all(|c| c.is_ascii_alphabetic())
}

fn text_settings(value: &Value) -> Result<()> {
    let bad = || invalid("textSettings", "contains invalid text or dimensions");
    let item = value.as_object().ok_or_else(|| invalid("textSettings", "must contain valid text settings"))?;
    let get = |pascal: &str| item.get(pascal).or_else(|| {
        let mut camel = pascal.to_string();
        camel[..1].make_ascii_lowercase();
        item.get(&camel)
    });
    let string = |key: &str| get(key).and_then(Value::as_str).map(str::to_string);
    let float = |key: &str, default: f64| match get(key) {
        None | Some(Value::Null) => Ok(default),
        Some(v) => v.as_f64().ok_or_else(|| invalid("textSettings", "must contain valid text settings")),
    };
    let text = string("Text").unwrap_or_default();
    let font = string("FontFamily").unwrap_or_default();
    let size = float("FontSize", 0.0)?;
    let outline = float("OutlineWidth", 0.0)?;
    let line = float("LineHeight", 0.0)?;
    let spacing = float("LetterSpacing", 0.0)?;
    let alignment = match get("Alignment") {
        None | Some(Value::Null) => 0,
        Some(Value::Number(n)) => n.as_i64().ok_or_else(bad)?,
        Some(Value::String(s)) => match s.as_str() { "Left" => 0, "Center" => 1, "Right" => 2, _ => return Err(bad()) },
        Some(_) => return Err(bad()),
    };
    if text.trim().is_empty() || text.encode_utf16().count() > 16384 || font.trim().is_empty() || font.chars().count() > 256
        || !(6.0..=600.0).contains(&size) || !(0.0..=40.0).contains(&outline) || !(0.0..=3000.0).contains(&line)
        || !(0.0..=200.0).contains(&spacing) || !(0..=2).contains(&alignment)
    {
        return Err(bad());
    }
    for key in ["FillColor", "OutlineColor"] {
        if !string(key).is_some_and(|c| css_color(&c)) {
            return Err(invalid("textSettings", "contains an invalid color"));
        }
    }
    for key in ["Bold", "Italic"] {
        if get(key).is_some_and(|v| !v.is_boolean() && !v.is_null()) {
            return Err(invalid("textSettings", "must contain valid text settings"));
        }
    }
    Ok(())
}

/// Port of `StudioProjectValidation.Validate`.
pub fn validate(project: &Value) -> Result<()> {
    let root = project.as_object().ok_or_else(|| Error::msg("Not a Court Creator project."))?;
    let version = number(root.get("version"), "version", true)?;
    let build_mode = text(root.get("buildMode"), "buildMode")?;
    let template = text(root.get("templatePath"), "templatePath")?;
    if !((version == 2.0 && build_mode == Some("game-uv")) || (version == 1.0 && template.is_some_and(|t| !t.is_empty()))) {
        return Err(invalid("version", "is not a supported Court Creator project"));
    }
    for key in ["_projectPath", "projectName", "templatePath", "mappingMode"] {
        text(root.get(key), key)?;
    }
    if let Some(mode) = text(root.get("assetPathMode"), "assetPathMode")? {
        if mode != "project-relative" {
            return Err(invalid("assetPathMode", "is not supported"));
        }
    }
    color(root.get("outsideColor"), "outsideColor")?;
    boolean(root.get("outsideVisible"), "outsideVisible")?;
    boolean(root.get("twoPointHardwoodEnabled"), "twoPointHardwoodEnabled")?;
    for floor_key in ["floor", "twoPointFloor"] {
        if root.get(floor_key).is_some_and(|v| !v.is_null()) {
            let floor = object(root.get(floor_key), floor_key)?;
            for key in ["id", "name", "path", "previewPath", "category"] {
                text(floor.get(key), &format!("{floor_key}.{key}"))?;
            }
            revision(floor.get("sourceRevision"), &format!("{floor_key}.sourceRevision"))?;
            artwork(floor)?;
            crate::hardwood::Settings::read(&Value::Object(floor.clone()))?;
        }
    }
    for key in ["paintSettings", "lineSettings"] {
        if let Some(entries) = map(root.get(key), key)? {
            for (id, value) in entries {
                let settings = object(Some(value), &format!("{key}.{id}"))?;
                boolean(settings.get("visible"), &format!("{key}.{id}.visible"))?;
                color(settings.get("color"), &format!("{key}.{id}.color"))?;
            }
        }
    }
    if let Some(entries) = map(root.get("visibility"), "visibility")? {
        for (id, value) in entries {
            boolean(Some(value), &format!("visibility.{id}"))?;
        }
    }
    if let Some(entries) = map(root.get("layerNames"), "layerNames")? {
        for (id, value) in entries {
            text(Some(value), &format!("layerNames.{id}"))?;
        }
    }
    if let Some(entries) = map(root.get("colorOverrides"), "colorOverrides")? {
        for (id, value) in entries {
            let rgb = value.as_array().filter(|a| a.len() == 3 || a.len() == 4).ok_or_else(|| invalid(&format!("colorOverrides.{id}"), "must contain three or four color channels"))?;
            for channel in rgb {
                let n = number(Some(channel), &format!("colorOverrides.{id}"), true)?;
                if !(0.0..=255.0).contains(&n) || n.trunc() != n {
                    return Err(invalid(&format!("colorOverrides.{id}"), "contains an invalid color channel"));
                }
            }
        }
    }
    let logos = array(root.get("logoImages"), "logoImages")?;
    if logos.len() > 256 {
        return Err(invalid("logoImages", "contains too many layers"));
    }
    let mut ids = std::collections::HashSet::new();
    for node in logos {
        let logo = object(Some(node), "logoImages layer")?;
        if let Some(id) = text(logo.get("id"), "logo.id")? {
            if !ids.insert(id.to_string()) {
                return Err(invalid("logo.id", "must be unique"));
            }
        }
        text(logo.get("name"), "logo.name")?;
        text(logo.get("path"), "logo.path")?;
        revision(logo.get("sourceRevision"), "logo.sourceRevision")?;
        artwork(logo)?;
        if let Some(settings) = logo.get("textSettings").filter(|v| !v.is_null()) {
            text_settings(settings)?;
        }
        for key in ["visible", "scaleLocked", "flipX", "flipY"] {
            boolean(logo.get(key), &format!("logo.{key}"))?;
        }
        for key in ["x", "y", "rotation"] {
            if logo.get(key).is_some_and(|v| !v.is_null()) && number(logo.get(key), &format!("logo.{key}"), false)?.abs() > 1_000_000.0 {
                return Err(invalid(&format!("logo.{key}"), "is outside the supported range"));
            }
        }
        for key in ["width", "height"] {
            if logo.get(key).is_some_and(|v| !v.is_null()) {
                let size = number(logo.get(key), &format!("logo.{key}"), false)?;
                if !(1.0..=32768.0).contains(&size) {
                    return Err(invalid(&format!("logo.{key}"), "must be between 1 and 32768"));
                }
            }
        }
        if logo.get("opacity").is_some_and(|v| !v.is_null()) {
            let opacity = number(logo.get("opacity"), "logo.opacity", false)?;
            if !(0.0..=100.0).contains(&opacity) {
                return Err(invalid("logo.opacity", "must be between 0 and 100"));
            }
        }
    }
    let customs = array(root.get("customFloorImages"), "customFloorImages")?;
    if customs.len() > 1024 {
        return Err(invalid("customFloorImages", "contains too many floors"));
    }
    ids.clear();
    for node in customs {
        let floor = object(Some(node), "customFloorImages layer")?;
        for key in ["id", "name", "path", "previewPath", "category"] {
            text(floor.get(key), &format!("customFloor.{key}"))?;
        }
        if let Some(id) = text(floor.get("id"), "customFloor.id")? {
            if !ids.insert(id.to_string()) {
                return Err(invalid("customFloor.id", "must be unique"));
            }
        }
        boolean(floor.get("visible"), "customFloor.visible")?;
        artwork(floor)?;
    }
    Ok(())
}

/// Reads and validates a project (16 MiB, depth 32, UTF-8 or BOM-marked UTF-16).
pub fn read(path: &Path) -> Result<Value> {
    let bytes = files::read_bounded(path, MAX_PROJECT_BYTES as u64).map_err(|error| Error::msg(format!("The court project could not be read: {error}")))?;
    let project = pyjson::parse(&bytes, MAX_PROJECT_BYTES, 32).map_err(|error| Error::msg(format!("Court project contains invalid JSON: {error}")))?;
    if !project.is_object() {
        return Err(Error::msg("Not a Court Creator project."));
    }
    validate(&project)?;
    Ok(project)
}

fn serialize(project: &Value) -> Result<Vec<u8>> {
    validate(project)?;
    let json = serde_json::to_string(project)?;
    pyjson::check_depth(&json, 32).map_err(|_| Error::msg("Court project contains unsupported nested data."))?;
    if json.len() > MAX_PROJECT_BYTES {
        return Err(Error::msg("Court project exceeds the 16 MB size limit."));
    }
    Ok(json.into_bytes())
}

/// Atomic project write; with `keep_backup` the previous valid project becomes `<path>.bak`.
pub fn write(path: &Path, project: &Value, keep_backup: bool, sources: &[PathBuf], validate_assets: Option<&dyn Fn() -> Result<()>>) -> Result<()> {
    let data = serialize(project)?;
    let path = files::normalized(path);
    let protect: Vec<&Path> = sources.iter().map(PathBuf::as_path).collect();
    files::ensure_new_export(&path, &protect)?;
    let backup = PathBuf::from(format!("{}.bak", path.display()));
    if keep_backup {
        files::ensure_new_export(&backup, &protect)?;
        if backup.exists() && read(&backup).is_err() {
            return Err(Error::msg("The project backup path contains an unrecognized or damaged file. Choose a different project name to preserve it."));
        }
    }
    let valid_existing = keep_backup && path.exists() && read(&path).is_ok();
    let (staged, mut file) = files::Staged::new(&path)?;
    file.write_all(&data)?;
    file.sync_all()?;
    drop(file);
    if let Some(check) = validate_assets {
        check()?;
    }
    files::ensure_new_export(&path, &protect)?;
    staged.publish(if valid_existing { Some(&backup) } else { None })
}

/// Paths of every artwork referenced by a snapshot, resolved like the original save code.
pub fn source_paths(project: &Value, application_root: &Path) -> Vec<PathBuf> {
    let basis = basis_directory(project, application_root);
    let mut paths = Vec::new();
    let mut add = |item: &Value| {
        for key in ["path", "previewPath", "artworkProjectPath", "artworkDdsPath"] {
            if let Some(path) = item.get(key).and_then(Value::as_str).filter(|p| !p.is_empty()) {
                paths.push(absolute(path, &basis));
            }
        }
    };
    for key in ["floor", "twoPointFloor"] {
        if let Some(item) = project.get(key).filter(|v| v.is_object()) {
            add(item);
        }
    }
    for key in ["logoImages", "customFloorImages"] {
        for item in project.get(key).and_then(Value::as_array).into_iter().flatten() {
            add(item);
        }
    }
    let mut unique: Vec<PathBuf> = Vec::new();
    for path in paths {
        if !unique.iter().any(|p| files::same_path(p, &path)) {
            unique.push(path);
        }
    }
    unique
}

fn basis_directory(project: &Value, application_root: &Path) -> PathBuf {
    if project.get("assetPathMode").and_then(Value::as_str) == Some("project-relative") {
        if let Some(original) = project.get("_projectPath").and_then(Value::as_str).filter(|p| !p.is_empty()) {
            if let Some(parent) = absolute(original, application_root).parent() {
                return parent.to_path_buf();
            }
        }
    }
    application_root.to_path_buf()
}

pub fn absolute(path: &str, basis: &Path) -> PathBuf {
    let candidate = PathBuf::from(path.replace('/', "\\"));
    if candidate.is_absolute() { candidate } else { basis.join(candidate) }
}

/// Copies one asset into `<project>.assets/<sha256><ext>`, reusing identical existing bundles.
fn bundle_asset(source: &Path, asset_directory: &Path, expected: Option<&str>, protected: &[&Path]) -> Result<(PathBuf, String)> {
    let mut input = std::fs::File::open(source)?;
    if input.metadata()?.len() > MAX_ASSET_BYTES {
        return Err(Error::msg("Project asset exceeds the 512 MB size limit."));
    }
    let mut bytes = Vec::new();
    input.read_to_end(&mut bytes)?;
    let hash = files::sha256_hex(&bytes);
    if expected.is_some_and(|e| e != hash) {
        return Err(Error::msg("Artwork changed after its preview was loaded. Refresh the court or reimport the logo before saving."));
    }
    let mut extension = source.extension().map(|e| format!(".{}", e.to_string_lossy().to_lowercase())).unwrap_or_default();
    if extension.len() > 16 || extension.chars().any(|c| c != '.' && !c.is_ascii_alphanumeric()) {
        extension = ".image".into();
    }
    std::fs::create_dir_all(asset_directory)?;
    let destination = asset_directory.join(format!("{hash}{extension}"));
    if destination.is_file() {
        if let Ok(existing) = files::read_bounded(&destination, MAX_ASSET_BYTES) {
            if files::sha256_hex(&existing) == hash {
                return Ok((destination, hash));
            }
        }
    }
    files::write_atomic(&destination, &bytes, protected)?;
    Ok((destination, hash))
}

fn relative(path: &Path, directory: &Path) -> String {
    match path.strip_prefix(directory) {
        Ok(rest) => rest.to_string_lossy().replace('\\', "/"),
        Err(_) => path.to_string_lossy().into_owned(),
    }
}

/// Port of `StudioProjectAssets.Save`: bundles artwork beside the project with relative paths.
pub fn save_portable(path: &Path, snapshot: &Value, application_root: &Path) -> Result<Value> {
    let path = files::normalized(path);
    let directory = path.parent().ok_or_else(|| Error::msg("Choose a project file."))?.to_path_buf();
    let stem = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_else(|| "Court".into());
    let asset_directory = directory.join(format!("{stem}.assets"));
    let mut project = snapshot.clone();
    validate(&project)?;
    let sources = source_paths(&project, application_root);
    let protected: Vec<&Path> = sources.iter().map(PathBuf::as_path).collect();
    files::ensure_new_export(&path, &protected)?;
    files::ensure_new_export(Path::new(&format!("{}.bak", path.display())), &protected)?;
    if asset_directory.is_symlink() {
        return Err(Error::msg("The portable asset folder is a link. Choose a project name with a regular asset folder."));
    }
    let source_directory = basis_directory(&project, application_root);
    let mut copied: HashMap<String, (String, String)> = HashMap::new();
    let mut bundle = |source: &str, expected: Option<&str>| -> Result<String> {
        if source.trim().is_empty() {
            return Ok(source.to_string());
        }
        let full = files::normalized(&absolute(source, &source_directory));
        let key = full.to_string_lossy().to_lowercase();
        if let Some((relative_path, hash)) = copied.get(&key) {
            if expected.is_some_and(|e| e != hash) {
                return Err(Error::msg("Artwork changed after its preview was loaded. Refresh the court or reimport the logo before saving."));
            }
            return Ok(relative_path.clone());
        }
        if !full.is_file() {
            if expected.is_some() {
                return Err(Error::msg("Artwork used in the preview is missing. Refresh the court or reimport the logo before saving."));
            }
            return Ok(full.to_string_lossy().into_owned());
        }
        let (bundled, hash) = bundle_asset(&full, &asset_directory, expected, &protected)?;
        let relative_path = relative(&bundled, &directory);
        copied.insert(key, (relative_path.clone(), hash));
        Ok(relative_path)
    };
    let mut bundle_item = |item: &mut Value, preview: bool| -> Result<()> {
        let Some(source) = item.get("path").and_then(Value::as_str).map(str::to_string) else { return Ok(()) };
        let expected = item.get("sourceRevision").and_then(Value::as_str).map(str::to_string);
        let bundled = bundle(&source, expected.as_deref())?;
        item["path"] = Value::String(bundled.clone());
        for key in ["artworkProjectPath", "artworkDdsPath"] {
            if let Some(artwork) = item.get(key).and_then(Value::as_str).filter(|p| !p.is_empty()).map(str::to_string) {
                let revision_key = key.replace("Path", "Revision");
                let expected = item.get(&revision_key).and_then(Value::as_str).map(str::to_string);
                item[key] = Value::String(bundle(&artwork, expected.as_deref())?);
            }
        }
        if preview && !source.trim().is_empty() && !Path::new(&bundled).is_absolute() {
            item["previewPath"] = Value::String(bundled);
        }
        Ok(())
    };
    for key in ["floor", "twoPointFloor"] {
        if let Some(item) = project.get_mut(key).filter(|v| v.is_object()) {
            bundle_item(item, true)?;
        }
    }
    if let Some(items) = project.get_mut("logoImages").and_then(Value::as_array_mut) {
        for item in items {
            bundle_item(item, false)?;
        }
    }
    if let Some(items) = project.get_mut("customFloorImages").and_then(Value::as_array_mut) {
        for item in items {
            bundle_item(item, true)?;
        }
    }
    project["assetPathMode"] = Value::String("project-relative".into());
    project["_projectPath"] = Value::String(path.to_string_lossy().into_owned());
    write(&path, &project, true, &sources, None)?;
    Ok(project)
}

// --- Recovery (app-owned slots: primary, .bak and last-good fallback) ---

pub fn recovery_path() -> PathBuf {
    files::data_root().join("recovery.json")
}

pub fn write_recovery(path: &Path, project: &Value) -> Result<()> {
    let backup = PathBuf::from(format!("{}.bak", path.display()));
    if !backup.exists() || read(&backup).is_ok() {
        return write(path, project, true, &[], None);
    }
    // A damaged backup is preserved; keep the previous valid recovery in a separate fallback slot.
    let fallback = PathBuf::from(format!("{}.last-good.json", path.display()));
    if let Some(previous) = [path.to_path_buf(), fallback.clone()].iter().find_map(|p| read(p).ok()) {
        write(&fallback, &previous, false, &[], None)?;
    }
    write(path, project, false, &[], None)
}

pub fn read_recovery(path: &Path) -> Option<Value> {
    let candidates = [path.to_path_buf(), PathBuf::from(format!("{}.bak", path.display())), PathBuf::from(format!("{}.last-good.json", path.display()))];
    candidates.iter().filter(|p| p.exists()).find_map(|p| read(p).ok())
}

/// Suggested Save As file name from the display name (invalid characters replaced).
pub fn suggested_file_name(name: &str) -> String {
    let invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    let safe: String = name.chars().map(|c| if invalid.contains(&c) || c.is_control() { '_' } else { c }).collect();
    let safe = safe.trim_end_matches(['.', ' ']).to_string();
    format!("{}.court.json", if safe.trim().is_empty() { "My Court".to_string() } else { safe })
}
