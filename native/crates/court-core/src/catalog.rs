//! Hardwood catalog: prepared stock floor library, custom floors and display names
//! (ports of backend.load_floor_template_layers / load_custom_floor_layers and StockFloor).
use crate::error::{Error, Result};
use crate::files;
use crate::pyjson;
use regex::Regex;
use serde::Serialize;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

pub const INDEX_RELATIVE: &str = "court_floor_templates/nba2k27/nba2k27_floor_templates.json";
const BROKEN_FLOOR_TEMPLATE_IDS: [&str; 2] = ["nba2k26-floor-300-court-wood1-basecolor", "nba2k27-floor-300-court-wood1-basecolor"];
const COLLEGE: [&str; 16] = ["arizonawildcats", "baylorbears", "dukebluedevils", "floridagators", "houstoncougars", "kansasjayhawks", "kentuckywildcats", "louisvillecardinals", "michiganstatespartans", "michiganwolverines", "ohiostatebuckeyes", "purdueboilermakers", "texaslonghorns", "uclabruins", "uconnhuskies", "unctarheels"];
const HISTORIC_NBA: [&str; 23] = ["bobcats2011", "bucks2015", "bulls2016", "cavaliers2011", "cavaliers2016", "clippers2015", "clippers2022", "grizzlies2016", "jazz2016", "kings2016", "knicks2016", "nets2012", "nuggets2016", "pacers2005", "pistons2016", "raptors2016", "rockets2003", "rockets2016", "spurs1998", "suns2016", "thunder2016", "timberwolves2011", "wizards2014"];
const INTERNATIONAL: [&str; 3] = ["barcelona", "madrid", "paris"];
const MODES: [&str; 7] = ["aau", "clutchtime", "gleagueignite", "matchmaking", "myteam", "scrimmage", "summerleaguegeneric"];
const NBA_IDS: [&str; 30] = ["000", "001", "002", "003", "004", "005", "006", "008", "009", "010", "011", "012", "013", "014", "015", "016", "017", "018", "019", "020", "021", "022", "023", "024", "025", "026", "027", "028", "029", "031"];
const WNBA_IDS: [&str; 15] = ["300", "301", "302", "303", "304", "305", "306", "307", "308", "309", "310", "311", "315", "316", "317"];
const HISTORIC_IDS: [&str; 57] = ["551", "552", "553", "554", "555", "556", "557", "558", "559", "560", "562", "564", "570", "571", "572", "574", "576", "583", "586", "588", "589", "590", "591", "592", "593", "594", "595", "620", "621", "622", "623", "624", "625", "626", "627", "628", "629", "630", "631", "632", "633", "634", "635", "636", "637", "638", "639", "641", "642", "644", "645", "646", "647", "648", "649", "924", "620"];
const EVENT_IDS: [&str; 16] = ["700", "701", "728", "729", "800", "852", "853", "854", "855", "856", "857", "858", "859", "860", "861", "906"];

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Floor {
    pub id: String,
    pub name: String,
    pub category: String,
    pub path: PathBuf,
    pub preview_path: PathBuf,
    /// Original catalog/project object (WPF `StockFloor.Source`), retained in snapshots.
    #[serde(skip)]
    pub source: Value,
}

fn floor_id_for_template(item: &Value) -> Option<String> {
    let token = [item.get("id"), item.get("name"), item.get("sourceMip0")]
        .iter()
        .map(|v| v.and_then(Value::as_str).unwrap_or(""))
        .collect::<Vec<_>>()
        .join(" ")
        .to_lowercase();
    static PATTERNS: LazyLock<[Regex; 3]> = LazyLock::new(|| [Regex::new(r"floor[_ -](\d{3})(?:\D|$)").unwrap(), Regex::new(r"\((\d{3})\)").unwrap(), Regex::new(r"\b(\d{3})\s+court\s+wood").unwrap()]);
    PATTERNS.iter().find_map(|pattern| pattern.captures(&token).map(|c| c[1].to_string()))
}

/// Port of `category_for_floor_template`.
pub fn category_for_template(item: &Value) -> String {
    let token = [item.get("id"), item.get("name"), item.get("sourceMip0")]
        .iter()
        .map(|v| v.and_then(Value::as_str).unwrap_or(""))
        .collect::<Vec<_>>()
        .join(" ")
        .to_lowercase();
    if token.contains("_city_") || token.contains(" city ") {
        return "City Edition".into();
    }
    if token.contains("_statement_") || token.contains(" statement ") {
        return "Statement Edition".into();
    }
    if token.contains("_classic_") || token.contains(" classic ") {
        return "Classic Edition".into();
    }
    if token.contains("wnba") {
        return "WNBA".into();
    }
    let floor_id = floor_id_for_template(item);
    let id = floor_id.as_deref().unwrap_or("");
    if WNBA_IDS.contains(&id) {
        return "WNBA".into();
    }
    if HISTORIC_IDS.contains(&id) {
        return "Historic NBA".into();
    }
    if EVENT_IDS.contains(&id) {
        return "All-Star & Events".into();
    }
    if floor_id.as_deref().is_some_and(|f| f.starts_with('4')) {
        return "Unknown".into();
    }
    if token.contains("allstar") || token.contains("_event_") || token.contains(" event ") {
        return "All-Star & Events".into();
    }
    if COLLEGE.iter().any(|k| token.contains(k)) {
        return "College".into();
    }
    if HISTORIC_NBA.iter().any(|k| token.contains(k)) {
        return "Historic NBA".into();
    }
    if INTERNATIONAL.iter().any(|k| token.contains(k)) {
        return "International".into();
    }
    if MODES.iter().any(|k| token.contains(k)) {
        return "Modes & Generic".into();
    }
    static NUMBERED: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"floor[_ -]\d+[_ -]court[_ -]wood").unwrap());
    if NBA_IDS.contains(&id) || NUMBERED.is_match(&token) {
        return "NBA".into();
    }
    let fallback = item.get("category").and_then(Value::as_str).unwrap_or("").trim().to_string();
    if !fallback.is_empty() && fallback != "Numbered Courts" {
        return fallback;
    }
    if fallback == "Numbered Courts" {
        return "NBA".into();
    }
    "Special".into()
}

static WOOD_TAG: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\bWood\s*(\d+)\b").unwrap());

/// `StockFloor.FriendlyName`: drop the Wood label and standard NBA arena numbers.
pub fn friendly_name(original: &str, category: &str) -> String {
    if category.eq_ignore_ascii_case("Custom") {
        return original.to_string();
    }
    static WOOD: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\s+(?:Court\s+)?Wood\s*\d+\b").unwrap());
    static ARENA: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\s*\((?:00\d|01\d|02\d|03[01])\)").unwrap());
    static SPACES: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\s{2,}").unwrap());
    let mut name = WOOD.replace_all(original, "").into_owned();
    if category.eq_ignore_ascii_case("NBA") {
        name = ARENA.replace_all(&name, "").into_owned();
    }
    let name = SPACES.replace_all(&name, " ").trim().to_string();
    if name.is_empty() { original.to_string() } else { name }
}

fn resolve(value: &str, root: &Path) -> PathBuf {
    let path = PathBuf::from(value.replace('/', "\\"));
    if path.is_absolute() { path } else { root.join(path) }
}

/// `StockFloor.Read` (with optional library for adopting the current variant label).
pub fn read_floor(item: &Value, root: &Path, library: Option<&[Floor]>) -> Result<Floor> {
    let category = item.get("category").and_then(Value::as_str).unwrap_or("Custom").to_string();
    let original = item.get("name").and_then(Value::as_str).ok_or_else(|| Error::msg("A hardwood entry has no name."))?;
    let id = item.get("id").and_then(Value::as_str).ok_or_else(|| Error::msg("A hardwood entry has no id."))?.to_string();
    let mut name = friendly_name(original, &category);
    if !category.eq_ignore_ascii_case("Custom") && WOOD_TAG.is_match(original) {
        if let Some(known) = library.and_then(|l| l.iter().find(|f| f.id == id && !f.category.eq_ignore_ascii_case("Custom"))) {
            let known_original = known.source.get("name").and_then(Value::as_str).unwrap_or(&known.name);
            if name.eq_ignore_ascii_case(&friendly_name(known_original, &known.category)) {
                name = known.name.clone();
            }
        }
    }
    let path = resolve(item.get("path").and_then(Value::as_str).ok_or_else(|| Error::msg("A hardwood entry has no path."))?, root);
    let preview_path = item.get("previewPath").and_then(Value::as_str).map(|p| resolve(p, root)).unwrap_or_else(|| path.clone());
    Ok(Floor { id, name, category, path, preview_path, source: item.clone() })
}

/// `StockFloor.ReadMany`: numbered variants for duplicate names within a category.
pub fn read_many(items: &[Value], root: &Path) -> Vec<Floor> {
    let mut floors: Vec<Floor> = items.iter().filter_map(|item| read_floor(item, root, None).ok()).collect();
    let mut by_category: HashMap<String, Vec<usize>> = HashMap::new();
    for (index, floor) in floors.iter().enumerate() {
        by_category.entry(floor.category.to_lowercase()).or_default().push(index);
    }
    for indices in by_category.values() {
        let mut used: HashSet<String> = indices.iter().map(|&i| floors[i].name.to_lowercase()).collect();
        let mut groups: Vec<(String, Vec<usize>)> = Vec::new();
        for &index in indices {
            let key = floors[index].name.to_lowercase();
            match groups.iter_mut().find(|(k, _)| *k == key) {
                Some((_, members)) => members.push(index),
                None => groups.push((key, vec![index])),
            }
        }
        for (_, members) in groups.into_iter().filter(|(_, m)| m.len() > 1) {
            let variant = |floor: &Floor| -> u32 {
                if floor.category.eq_ignore_ascii_case("Custom") {
                    return 0;
                }
                let original = floor.source.get("name").and_then(Value::as_str).unwrap_or("");
                WOOD_TAG.captures(original).and_then(|c| c[1].parse::<u32>().ok()).filter(|v| *v > 0).unwrap_or(0)
            };
            let mut ordered: Vec<(usize, u32)> = members.iter().map(|&i| (i, variant(&floors[i]))).collect();
            ordered.sort_by(|a, b| {
                let va = if a.1 > 0 { a.1 } else { u32::MAX };
                let vb = if b.1 > 0 { b.1 } else { u32::MAX };
                va.cmp(&vb).then(floors[a.0].id.cmp(&floors[b.0].id)).then(floors[a.0].path.cmp(&floors[b.0].path))
            });
            let base = floors[ordered[0].0].name.clone();
            for (index, variant) in ordered {
                let mut suffix = variant;
                if suffix == 0 || used.contains(&format!("{base} {suffix}").to_lowercase()) {
                    suffix = 1;
                }
                while !used.insert(format!("{base} {suffix}").to_lowercase()) {
                    suffix += 1;
                }
                floors[index].name = format!("{base} {suffix}");
            }
        }
    }
    floors
}

#[derive(Clone, Debug)]
pub struct Library {
    pub name: String,
    pub assets_root: PathBuf,
    pub complete: bool,
    /// Catalog rows in the backend's `customFloorImages` shape (absolute paths).
    pub images: Vec<Value>,
}

/// Port of `load_floor_template_layers` for one prepared library root (`.../assets`).
pub fn load_library(assets_root: &Path) -> Option<Library> {
    let index = assets_root.join(INDEX_RELATIVE);
    let data = pyjson::read_file(&index, 8 * 1024 * 1024, 32).ok()?;
    let templates = data.get("templates")?.as_array()?;
    let mut images = Vec::new();
    for item in templates {
        let Some(path_text) = item.get("path").and_then(Value::as_str).filter(|p| !p.is_empty() && !p.contains('\0')) else { continue };
        let id = item.get("id").and_then(Value::as_str).unwrap_or("").to_string();
        if BROKEN_FLOOR_TEMPLATE_IDS.contains(&id.as_str()) {
            continue;
        }
        let path = resolve(path_text, assets_root);
        if !path.is_file() {
            continue;
        }
        let category = category_for_template(item);
        let name = item.get("name").and_then(Value::as_str).map(str::to_string).unwrap_or_else(|| path.file_stem().unwrap_or_default().to_string_lossy().into_owned());
        let thumbnail = item.get("thumbnailPath").and_then(Value::as_str).map(|t| resolve(t, assets_root)).filter(|t| t.is_file());
        let index = images.len();
        images.push(json!({
            "id": if id.is_empty() { format!("floor_template_{index}") } else { id },
            "name": name,
            "path": path.to_string_lossy(),
            "previewPath": thumbnail.unwrap_or(path.clone()).to_string_lossy(),
            "bbox": Value::Null,
            "isTemplate": true,
            "category": category,
            "sourceMip0": item.get("sourceMip0").cloned().unwrap_or(Value::Null),
            "sourceTld": item.get("sourceTld").cloned().unwrap_or(Value::Null),
        }));
    }
    if images.is_empty() {
        return None;
    }
    let name = data.get("name").and_then(Value::as_str).unwrap_or("NBA 2K27 Courts").replace(" Floor Templates", " Courts");
    let complete = data.get("preparationComplete").and_then(Value::as_bool).unwrap_or(true)
        && templates.iter().all(|item| {
            ["path", "thumbnailPath"].iter().all(|key| item.get(*key).and_then(Value::as_str).is_some_and(|p| resolve(p, assets_root).is_file()))
        });
    Some(Library { name, assets_root: assets_root.to_path_buf(), complete, images })
}

/// Custom floors stored by `add_custom_floor` (`custom_floors/custom_floors.json`).
pub fn load_custom(root: &Path) -> Vec<Value> {
    let meta = root.join("custom_floors").join("custom_floors.json");
    let Ok(data) = pyjson::read_file(&meta, 8 * 1024 * 1024, 32) else { return Vec::new() };
    let mut out = Vec::new();
    for item in data.get("floors").and_then(Value::as_array).into_iter().flatten() {
        let Some(path_text) = item.get("path").and_then(Value::as_str).filter(|p| !p.is_empty() && !p.contains('\0')) else { continue };
        let path = resolve(path_text, root);
        if !path.is_file() {
            continue;
        }
        let stem = path.file_stem().unwrap_or_default().to_string_lossy().into_owned();
        out.push(json!({
            "id": item.get("id").and_then(Value::as_str).map(str::to_string).unwrap_or(format!("custom_floor_{stem}")),
            "name": item.get("name").and_then(Value::as_str).map(str::to_string).unwrap_or(stem),
            "path": path.to_string_lossy(),
            "previewPath": path.to_string_lossy(),
            "bbox": item.get("bbox").cloned().unwrap_or(Value::Null),
        }));
    }
    out
}

pub fn safe_stem(value: &str) -> String {
    let safe: String = value.trim().chars().map(|c| if c.is_alphanumeric() || c == '-' || c == '_' { c } else { '-' }).collect();
    let safe = safe.trim_matches(|c| c == '-' || c == '_').to_lowercase();
    if safe.is_empty() { "custom-floor".into() } else { safe }
}

/// Adds a custom floor image to the app-owned store, keeping existing catalog rows and fields.
pub fn add_custom(root: &Path, source: &Path) -> Result<Value> {
    if !source.is_file() {
        return Err(Error::msg("Custom floor image was not found."));
    }
    let bytes = files::read_bounded(source, files::MAX_ASSET_BYTES)?;
    let image = crate::raster::decode(&bytes, source.extension().and_then(|e| e.to_str()))?;
    drop(image);
    let directory = root.join("custom_floors");
    std::fs::create_dir_all(&directory)?;
    let meta = directory.join("custom_floors.json");
    let mut catalog = if meta.exists() { pyjson::read_file(&meta, 8 * 1024 * 1024, 32)? } else { json!({"floors": []}) };
    if !catalog.get("floors").is_some_and(Value::is_array) {
        return Err(Error::msg("The custom floor catalog is damaged. Its file was left unchanged."));
    }
    let stem = safe_stem(&source.file_stem().unwrap_or_default().to_string_lossy());
    let suffix = source.extension().map(|e| format!(".{}", e.to_string_lossy().to_lowercase())).unwrap_or_else(|| ".png".into());
    let mut destination = directory.join(format!("{stem}{suffix}"));
    let mut counter = 2;
    while destination.exists() {
        destination = directory.join(format!("{stem}-{counter}{suffix}"));
        counter += 1;
    }
    files::write_atomic(&destination, &bytes, &[source])?;
    let file_stem = destination.file_stem().unwrap().to_string_lossy().into_owned();
    let name = file_stem.replace(['-', '_'], " ");
    let name = name.split_whitespace().map(|w| {
        let mut chars = w.chars();
        chars.next().map(|c| c.to_uppercase().collect::<String>() + &chars.as_str().to_lowercase()).unwrap_or_default()
    }).collect::<Vec<_>>().join(" ");
    let entry = json!({
        "id": format!("custom_floor_{file_stem}"),
        "name": name,
        "path": format!("custom_floors\\{}", destination.file_name().unwrap().to_string_lossy()),
        "previewPath": destination.to_string_lossy(),
    });
    catalog["floors"].as_array_mut().unwrap().push(entry.clone());
    let payload = pyjson::dumps(&catalog, pyjson::Style::PRETTY_UNICODE);
    if let Err(error) = files::write_atomic(&meta, payload.as_bytes(), &[source]) {
        let _ = std::fs::remove_file(&destination);
        return Err(error);
    }
    let mut image = entry;
    image["path"] = Value::String(destination.to_string_lossy().into_owned());
    Ok(image)
}

/// Locates usable floor libraries: the preview's own first, then validated existing ones (read only).
pub fn library_roots() -> Vec<PathBuf> {
    let mut roots = vec![files::data_root().join("library")];
    roots.extend(crate::legacy::roots().into_iter().map(|r| r.join("assets")));
    roots
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn friendly_names_and_variants() {
        assert_eq!(friendly_name("Philadelphia 76ers (000) Court Wood1", "NBA"), "Philadelphia 76ers");
        assert_eq!(friendly_name("Milwaukee Bucks (001) Statement Court Wood1", "Statement Edition"), "Milwaukee Bucks (001) Statement");
        let items = vec![
            json!({"id": "b", "name": "Top 10 Rep Court Wood2", "path": "x2.png", "category": "Special"}),
            json!({"id": "a", "name": "Top 10 Rep Court Wood1", "path": "x1.png", "category": "Special"}),
        ];
        let floors = read_many(&items, Path::new("C:\\"));
        assert_eq!(floors[0].name, "Top 10 Rep 2");
        assert_eq!(floors[1].name, "Top 10 Rep 1");
    }
    #[test]
    fn categories() {
        assert_eq!(category_for_template(&json!({"id": "nba2k27-floor-000-court-wood1-basecolor", "name": "Philadelphia 76ers (000) Court Wood1"})), "NBA");
        assert_eq!(category_for_template(&json!({"id": "nba2k27-floor-301-court-basecolor", "name": "Atlanta Dream (301) Court Wood1"})), "WNBA");
        assert_eq!(category_for_template(&json!({"id": "x", "name": "Duke Blue Devils Court Wood1", "category": "College"})), "College");
    }
}
