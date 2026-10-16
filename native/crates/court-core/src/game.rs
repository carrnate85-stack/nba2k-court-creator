//! Installed NBA 2K27 discovery (saved choice, environment, Steam libraries) and read-only
//! extraction through the game's own `mod.exe`/`manifest` into private scratch folders.
use crate::error::{Error, Result, check};
use crate::files;
use crate::pyjson;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

pub const MAX_SETTINGS_BYTES: usize = 1024 * 1024;

pub fn usable_root(path: &Path) -> Option<PathBuf> {
    if path.join("mod.exe").is_file() && path.join("manifest").is_file() {
        Some(files::normalized(path))
    } else {
        None
    }
}

fn discovery_path(value: Option<&str>) -> Option<PathBuf> {
    let value = value?;
    if value.trim().is_empty() || value.len() > 32768 || value.contains('\0') {
        return None;
    }
    Some(PathBuf::from(value))
}

pub fn settings_path() -> PathBuf {
    files::data_root().join("game_installation.json")
}

fn saved_root() -> Option<PathBuf> {
    let value = pyjson::read_file(&settings_path(), MAX_SETTINGS_BYTES, 32).ok()?;
    discovery_path(value.get("nba2k27Root").and_then(Value::as_str))
}

/// Remembers a usable folder in the preview's own settings, keeping other fields.
pub fn remember_root(root: &Path) -> Result<()> {
    let path = settings_path();
    let mut document = if path.exists() {
        let value = pyjson::read_file(&path, MAX_SETTINGS_BYTES, 32).map_err(|e| Error::msg(format!("The existing game installation settings are damaged; repair or rename {}: {e}", path.display())))?;
        if !value.is_object() {
            return Err(Error::msg(format!("Game installation settings must be an object; their file was left unchanged: {}", path.display())));
        }
        value
    } else {
        json!({})
    };
    let root_text = root.to_string_lossy().into_owned();
    if document.get("nba2k27Root").and_then(Value::as_str) == Some(root_text.as_str()) {
        return Ok(());
    }
    document["nba2k27Root"] = Value::String(root_text);
    let payload = pyjson::dumps(&document, pyjson::Style::COMPACT);
    files::write_atomic(&path, payload.as_bytes(), &[&root.join("mod.exe"), &root.join("manifest")])
}

#[cfg(windows)]
fn registry_steam_roots() -> Vec<PathBuf> {
    use windows::Win32::System::Registry::{HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW};
    use windows::core::HSTRING;
    let mut roots = Vec::new();
    for (hive, key, value) in [(HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"), (HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")] {
        let mut buffer = vec![0u16; 1024];
        let mut size = (buffer.len() * 2) as u32;
        let status = unsafe { RegGetValueW(HKEY(hive.0), &HSTRING::from(key), &HSTRING::from(value), RRF_RT_REG_SZ, None, Some(buffer.as_mut_ptr() as *mut _), Some(&mut size)) };
        if status.is_ok() {
            let length = (size as usize / 2).saturating_sub(1);
            if let Some(path) = discovery_path(Some(&String::from_utf16_lossy(&buffer[..length.min(buffer.len())]))) {
                roots.push(path);
            }
        }
    }
    roots
}
#[cfg(not(windows))]
fn registry_steam_roots() -> Vec<PathBuf> {
    Vec::new()
}

/// Bounded Valve KeyValues reader for `libraryfolders.vdf` (port of `_parse_vdf`).
pub fn parse_vdf(text: &str) -> Result<Value> {
    if text.len() > MAX_SETTINGS_BYTES {
        return Err(Error::msg("Steam libraries exceed the 1 MiB size limit."));
    }
    let mut tokens: Vec<String> = Vec::new();
    let bytes = text.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        let c = bytes[i];
        if c.is_ascii_whitespace() {
            i += 1;
        } else if c == b'/' && bytes.get(i + 1) == Some(&b'/') {
            while i < bytes.len() && bytes[i] != b'\n' && bytes[i] != b'\r' {
                i += 1;
            }
        } else if c == b'"' {
            let start = i;
            i += 1;
            while i < bytes.len() && bytes[i] != b'"' {
                i += if bytes[i] == b'\\' { 2 } else { 1 };
            }
            if i >= bytes.len() {
                return Err(Error::msg("Invalid token or unclosed quoted string in Steam libraries."));
            }
            i += 1;
            tokens.push(text[start..i].to_string());
        } else if c == b'{' || c == b'}' {
            tokens.push((c as char).to_string());
            i += 1;
        } else {
            let start = i;
            while i < bytes.len() && !bytes[i].is_ascii_whitespace() && bytes[i] != b'{' && bytes[i] != b'}' && bytes[i] != b'"' {
                i += 1;
            }
            tokens.push(text[start..i].to_string());
        }
        if tokens.len() > 65536 {
            return Err(Error::msg("Steam libraries exceed the token limit."));
        }
    }
    let unquote = |token: &str| -> Result<String> {
        if token.starts_with('"') {
            serde_json::from_str::<String>(token).map_err(|_| Error::msg("Invalid quoted string in Steam libraries."))
        } else {
            Ok(token.to_string())
        }
    };
    fn read(tokens: &[String], cursor: &mut usize, nested: bool, depth: usize, unquote: &dyn Fn(&str) -> Result<String>) -> Result<Value> {
        if depth > 32 {
            return Err(Error::msg("Steam library nesting exceeds the 32-level limit."));
        }
        let mut result = serde_json::Map::new();
        while *cursor < tokens.len() {
            let token = &tokens[*cursor];
            *cursor += 1;
            if token == "}" {
                if !nested {
                    return Err(Error::msg("Unexpected closing brace in Steam libraries."));
                }
                return Ok(Value::Object(result));
            }
            if token == "{" {
                return Err(Error::msg("Missing Steam library key."));
            }
            let key = unquote(token)?;
            let value = tokens.get(*cursor).ok_or_else(|| Error::msg("Missing Steam library value."))?.clone();
            *cursor += 1;
            if value == "}" {
                return Err(Error::msg("Missing Steam library value."));
            }
            let parsed = if value == "{" { read(tokens, cursor, true, depth + 1, unquote)? } else { Value::String(unquote(&value)?) };
            result.insert(key, parsed);
        }
        if nested {
            return Err(Error::msg("Unclosed Steam library object."));
        }
        Ok(Value::Object(result))
    }
    let mut cursor = 0;
    read(&tokens, &mut cursor, false, 1, &unquote)
}

fn steam_libraries(steam_root: &Path) -> Vec<PathBuf> {
    let mut libraries = vec![steam_root.to_path_buf()];
    let file = steam_root.join("steamapps").join("libraryfolders.vdf");
    if let Ok(bytes) = files::read_bounded(&file, MAX_SETTINGS_BYTES as u64) {
        if let Ok(text) = pyjson::decode_text(&bytes) {
            if let Ok(data) = parse_vdf(&text) {
                if let Some(folders) = data.get("libraryfolders").and_then(Value::as_object) {
                    for (key, value) in folders {
                        if key.chars().all(|c| c.is_ascii_digit()) && !key.is_empty() {
                            let path = value.get("path").and_then(Value::as_str).or_else(|| value.as_str());
                            if let Some(path) = discovery_path(path) {
                                libraries.push(path);
                            }
                        }
                    }
                }
            }
        }
    }
    libraries
}

/// Port of `find_nba2k27_root`: explicit choice, saved settings, NBA2K27_ROOT, then Steam.
pub fn find_root(explicit: Option<&Path>) -> Result<PathBuf> {
    if let Some(path) = explicit {
        return usable_root(path).ok_or_else(|| Error::msg("NBA 2K27 was not found in the selected folder. Choose the folder containing mod.exe and manifest."));
    }
    let mut candidates: Vec<PathBuf> = Vec::new();
    candidates.extend(saved_root());
    candidates.extend(crate::legacy::saved_game_roots());
    candidates.extend(discovery_path(std::env::var("NBA2K27_ROOT").ok().as_deref()));
    for candidate in &candidates {
        if let Some(root) = usable_root(candidate) {
            return Ok(root);
        }
    }
    let mut steam_roots = vec![
        PathBuf::from(std::env::var("ProgramFiles(x86)").unwrap_or_else(|_| r"C:\Program Files (x86)".into())).join("Steam"),
        PathBuf::from(std::env::var("ProgramFiles").unwrap_or_else(|_| r"C:\Program Files".into())).join("Steam"),
    ];
    steam_roots.extend(registry_steam_roots());
    for steam in steam_roots {
        for library in steam_libraries(&steam) {
            if let Some(root) = usable_root(&library.join("steamapps").join("common").join("NBA 2K27")) {
                return Ok(root);
            }
        }
    }
    Err(Error::msg("NBA 2K27 was not found. Install it through Steam or choose its game folder (the folder containing mod.exe and manifest)."))
}

#[derive(Clone, Debug)]
pub struct ManifestEntry {
    pub path: String,
    pub chunk: String,
    pub offset: u64,
    pub size: u64,
}

/// Rows of `path,chunk,offset,size` (other rows are ignored, as in the extraction tool).
pub fn read_manifest(root: &Path) -> Result<Vec<ManifestEntry>> {
    let data = std::fs::read(root.join("manifest"))?;
    let text = String::from_utf8_lossy(&data);
    let mut rows = Vec::new();
    for line in text.lines() {
        let parts: Vec<&str> = line.trim_end_matches('\r').split(',').collect();
        if parts.len() != 4 {
            continue;
        }
        let (Ok(offset), Ok(size)) = (parts[2].trim().parse::<i64>(), parts[3].trim().parse::<i64>()) else { continue };
        if offset < 0 || size < 0 {
            continue;
        }
        rows.push(ManifestEntry { path: parts[0].to_string(), chunk: parts[1].to_string(), offset: offset as u64, size: size as u64 });
    }
    Ok(rows)
}

/// Copies a manifest member's raw bytes from its game archive (read-only).
pub fn read_member(root: &Path, entry: &ManifestEntry, max_bytes: u64) -> Result<Vec<u8>> {
    use std::io::{Read, Seek, SeekFrom};
    if entry.size == 0 || entry.size > max_bytes {
        return Err(Error::msg("Invalid texture range in the game manifest."));
    }
    let chunk = files::normalized(&root.join(&entry.chunk));
    if !chunk.starts_with(files::normalized(root)) {
        return Err(Error::msg("Archive path leaves the game folder."));
    }
    let mut file = std::fs::File::open(&chunk)?;
    file.seek(SeekFrom::Start(entry.offset))?;
    let mut data = vec![0u8; entry.size as usize];
    file.read_exact(&mut data).map_err(|_| Error::msg("Unexpected end of package while extracting a manifest entry."))?;
    Ok(data)
}

/// Private scratch folder, removed on drop.
pub struct Scratch {
    directory: PathBuf,
    file: PathBuf,
}
impl Scratch {
    pub fn path(&self) -> &Path {
        &self.file
    }
    pub fn directory(&self) -> &Path {
        &self.directory
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.directory);
    }
}

pub fn scratch_directory(prefix: &str) -> Result<PathBuf> {
    let directory = files::data_root().join("scratch").join(format!("{prefix}{}", uuid::Uuid::new_v4().simple()));
    std::fs::create_dir_all(&directory)?;
    Ok(directory)
}

fn run_tool(program: &Path, arguments: &[&str], cwd: &Path, timeout: Duration, cancelled: &dyn Fn() -> bool) -> Result<(i32, String)> {
    let mut command = Command::new(program);
    command.args(arguments).current_dir(cwd).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let mut child = command.spawn().map_err(|e| Error::msg(format!("{} could not start: {e}", program.display())))?;
    let output = std::sync::Arc::new(std::sync::Mutex::new(Vec::<u8>::new()));
    let mut readers = Vec::new();
    for stream in [child.stdout.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>), child.stderr.take().map(|s| Box::new(s) as Box<dyn std::io::Read + Send>)].into_iter().flatten() {
        let output = output.clone();
        readers.push(std::thread::spawn(move || {
            let mut stream = stream;
            let mut buffer = [0u8; 8192];
            let mut total = 0usize;
            while let Ok(read) = stream.read(&mut buffer) {
                if read == 0 {
                    break;
                }
                total += read;
                if let Ok(mut out) = output.lock() {
                    out.extend_from_slice(&buffer[..read]);
                    let length = out.len();
                    if length > 64 * 1024 {
                        out.drain(..length - 64 * 1024);
                    }
                }
                if total > 16 * 1024 * 1024 {
                    break;
                }
            }
        }));
    }
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if cancelled() || started.elapsed() > timeout {
            let _ = child.kill();
            let _ = child.wait();
            return Err(if cancelled() { Error::cancelled() } else { Error::msg(format!("{} did not finish in time.", program.display())) });
        }
        std::thread::sleep(Duration::from_millis(50));
    };
    for reader in readers {
        let _ = reader.join();
    }
    let text = String::from_utf8_lossy(&output.lock().map(|o| o.clone()).unwrap_or_default()).into_owned();
    Ok((status.code().unwrap_or(-1), text))
}

/// Port of `extracted_stock_floor`: expands one level IFF with the game's extractor into scratch
/// space through a rewritten manifest; installed archives, manifest and mods stay unchanged.
pub fn extract_stock_floor(root: &Path, entry: &str, cancelled: &dyn Fn() -> bool) -> Result<Scratch> {
    let valid = regex::Regex::new(r"^levels/[a-zA-Z0-9_]+_floor\.iff$").unwrap();
    if !valid.is_match(entry) {
        return Err(Error::msg("Invalid stock floor entry."));
    }
    let root = files::normalized(root);
    let directory = scratch_directory("court-stock-floor-")?;
    let scratch = Scratch { directory: directory.clone(), file: directory.join("mods").join(entry.replace('/', "\\")) };
    let source = std::fs::read(root.join("manifest"))?;
    let text = String::from_utf8(source).map_err(|_| Error::msg("The NBA 2K27 manifest is not UTF-8 text."))?;
    let mut found = false;
    let mut archives: std::collections::HashMap<String, String> = std::collections::HashMap::new();
    let mut rewritten = String::with_capacity(text.len() + text.len() / 4);
    for line in text.split_inclusive('\n') {
        let body = line.trim_end_matches(['\n', '\r']);
        let mut parts: Vec<String> = body.split(',').map(str::to_string).collect();
        if parts.len() == 4 {
            if parts[0].replace('\\', "/").eq_ignore_ascii_case(entry) {
                if found {
                    return Err(Error::msg("The stock floor has duplicate manifest entries."));
                }
                let (offset, size) = (parts[2].trim().parse::<i64>(), parts[3].trim().parse::<i64>());
                if !matches!((offset, size), (Ok(o), Ok(s)) if o >= 0 && s > 0) {
                    return Err(Error::msg("The stock floor has an invalid manifest range."));
                }
                found = true;
            }
            let archive = parts[1].clone();
            let resolved = archives.entry(archive.clone()).or_insert_with(|| root.join(&archive).to_string_lossy().into_owned()).clone();
            parts[1] = resolved;
            rewritten.push_str(&parts.join(","));
        } else {
            rewritten.push_str(body);
        }
        rewritten.push('\n');
    }
    if !found {
        return Err(Error::msg("The NBA 2K27 manifest does not contain the stock export floor."));
    }
    std::fs::write(directory.join("manifest"), rewritten)?;
    let mut last = String::new();
    for _attempt in 0..2 {
        check(cancelled)?;
        let _ = std::fs::remove_file(scratch.path());
        let (code, output) = run_tool(&root.join("mod.exe"), &[entry], &directory, Duration::from_secs(180), cancelled)?;
        if !files::normalized(scratch.path()).starts_with(files::normalized(&directory)) {
            return Err(Error::msg("Stock extraction returned a path outside its scratch folder."));
        }
        let is_zip = std::fs::read(scratch.path()).map(|d| d.len() > 4 && d.starts_with(b"PK")).unwrap_or(false);
        if code == 0 && is_zip {
            return Ok(scratch);
        }
        last = output;
    }
    let detail = if last.is_empty() { "No output was produced.".to_string() } else { last.chars().rev().take(1200).collect::<Vec<_>>().into_iter().rev().collect() };
    Err(Error::msg(format!("NBA 2K27 could not expand the stock floor. {detail}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn steam_library_formats() {
        let text = "\"libraryfolders\"\n{\n  \"0\" { \"path\" \"C:\\\\Program Files (x86)\\\\Steam\" // main\n }\n \"1\" \"D:\\\\Games\"\n}\n";
        let value = parse_vdf(text).unwrap();
        assert_eq!(value["libraryfolders"]["0"]["path"], "C:\\Program Files (x86)\\Steam");
        assert_eq!(value["libraryfolders"]["1"], "D:\\Games");
        assert!(parse_vdf("\"a\" {").is_err());
    }
}
