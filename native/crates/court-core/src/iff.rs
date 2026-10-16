//! IFF inspection, the cleaned regular export base and texture packaging
//! (ports of court_import.inspect_iff / prepare_2k27_base / package_png_into_iff).
use crate::error::{Error, Result, check};
use crate::files;
use crate::floor_geometry;
use crate::geometry::{BASE_ENTRY, Geometry};
use crate::scene::{self, SCENE_ENTRY, Scene};
use crate::ziparc::{self, Archive};
use serde::Serialize;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

pub const BASE_VERSION: &[u8] = b"CourtCreator-FullCourt-v3";
pub const LAYOUT_PREFIX: &[u8] = b"CourtCreator-Layout-v1\n";
pub const MAX_BASE_ARCHIVE_BYTES: u64 = 512 * 1024 * 1024;
pub const MAX_DDS_BYTES: u64 = 256 * 1024 * 1024;

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Texture {
    pub name: String,
    pub width: u32,
    pub height: u32,
    pub format: String,
    pub mipmaps: u32,
    pub bytes: u64,
}

fn dxgi_name(dxgi: u32) -> Option<&'static str> {
    Some(match dxgi {
        71 => "BC1_UNORM",
        72 => "BC1_UNORM_SRGB",
        77 => "BC3_UNORM",
        78 => "BC3_UNORM_SRGB",
        98 => "BC7_UNORM",
        99 => "BC7_UNORM_SRGB",
        _ => return None,
    })
}

fn u32le(data: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(data[offset..offset + 4].try_into().unwrap())
}

/// Port of `_dds_header_info` (single complete 2D surfaces only).
pub fn dds_header_info(header: &[u8], byte_count: u64, name: &str, exact_payload: bool) -> Option<Texture> {
    if !(128..=MAX_DDS_BYTES).contains(&byte_count) || header.len() < 128 || &header[..4] != b"DDS " {
        return None;
    }
    if u32le(header, 4) != 124 || u32le(header, 76) != 32 {
        return None;
    }
    let (height, width) = (u32le(header, 12), u32le(header, 16));
    if !(16..=16384).contains(&width) || !(16..=16384).contains(&height) {
        return None;
    }
    let fourcc = &header[84..88];
    let (depth, mipmaps) = (u32le(header, 24), u32le(header, 28));
    let bit_length = 32 - width.max(height).leading_zeros();
    if depth > 1 || u32le(header, 112) & (0xFE00 | 0x200000) != 0 || mipmaps > bit_length {
        return None;
    }
    if exact_payload && (u32le(header, 80) & 4 == 0 || u32le(header, 8) & 0x800000 != 0) {
        return None;
    }
    let mut block_bytes: Option<u64> = match fourcc {
        b"DXT1" | b"ATI1" | b"BC4U" | b"BC4S" => Some(8),
        b"DXT3" | b"DXT5" | b"ATI2" | b"BC5U" | b"BC5S" => Some(16),
        _ => None,
    };
    let mut header_bytes = 128u64;
    let mut dxgi = 0u32;
    let format = if fourcc == b"DX10" {
        if header.len() < 148 {
            return None;
        }
        dxgi = u32le(header, 128);
        let (dimension, misc, arrays) = (u32le(header, 132), u32le(header, 136), u32le(header, 140));
        if dimension != 3 || misc & 4 != 0 || arrays != 1 {
            return None;
        }
        block_bytes = if (70..=72).contains(&dxgi) || (79..=81).contains(&dxgi) { Some(8) } else if (73..=78).contains(&dxgi) || (82..=84).contains(&dxgi) || (94..=99).contains(&dxgi) { Some(16) } else { None };
        header_bytes = 148;
        dxgi_name(dxgi).unwrap_or("Other DX10").to_string()
    } else {
        match fourcc {
            b"DXT1" => "BC1_UNORM",
            b"DXT5" => "BC3_UNORM",
            _ => "Other",
        }
        .to_string()
    };
    let levels = mipmaps.max(1);
    if let Some(block) = block_bytes {
        let required: u64 = header_bytes + (0..levels).map(|l| ((width >> l).max(1).div_ceil(4)).max(1) as u64 * ((height >> l).max(1).div_ceil(4)).max(1) as u64 * block).sum::<u64>();
        if byte_count < required || (exact_payload && byte_count != required) {
            return None;
        }
    } else {
        let flags = u32le(header, 80);
        let bit_count = u32le(header, 88);
        let bytes_per_pixel = if fourcc == b"DX10" && (27..=29).contains(&dxgi) {
            Some(4)
        } else if flags & (0x40 | 0x20000) != 0 && [8, 16, 24, 32].contains(&bit_count) {
            Some(bit_count as u64 / 8)
        } else if flags & 0x20 != 0 {
            Some(1)
        } else {
            None
        };
        if let Some(bpp) = bytes_per_pixel {
            let palette = if flags & 0x20 != 0 { 1024 } else { 0 };
            let required: u64 = header_bytes + palette + (0..levels).map(|l| (width >> l).max(1) as u64 * (height >> l).max(1) as u64 * bpp).sum::<u64>();
            if byte_count < required || (exact_payload && byte_count != required) {
                return None;
            }
        } else if exact_payload {
            return None;
        }
    }
    Some(Texture { name: name.to_string(), width, height, format, mipmaps, bytes: byte_count })
}

pub fn dds_info(archive: &Archive, name: &str) -> Option<Texture> {
    let entry = archive.entry(name)?;
    if entry.size < 128 || entry.size > MAX_DDS_BYTES {
        return None;
    }
    let header = archive.read_prefix(name, 148).ok()?;
    dds_header_info(&header, entry.size, name, false)
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Inspection {
    pub path: String,
    pub textures: Vec<Texture>,
    pub selected: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_bounds: Option<[i64; 4]>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_revision: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub alignment_source: Option<String>,
}

pub fn is_target_format(format: &str) -> bool {
    matches!(format, "BC1_UNORM" | "BC1_UNORM_SRGB" | "BC3_UNORM" | "BC3_UNORM_SRGB" | "BC7_UNORM" | "BC7_UNORM_SRGB")
}

/// Lists readable DDS members, sorted like the original (2:1 court textures first).
pub fn textures(archive: &Archive, target: bool) -> Result<Vec<Texture>> {
    let mut list: Vec<Texture> = archive.names().filter(|n| n.to_lowercase().ends_with(".dds")).filter_map(|n| dds_info(archive, n)).collect();
    if target {
        list.retain(|t| t.width == 8192 && t.height == 4096 && is_target_format(&t.format));
        if list.is_empty() {
            return Err(Error::msg("This IFF has no replaceable 8192 x 4096 court DDS. Choose a known-working 2K27 floor IFF."));
        }
    } else if list.is_empty() {
        return Err(Error::msg("No readable DDS textures were found in this IFF."));
    }
    let key = |t: &Texture| (t.width as f64 / t.height as f64 == 2.0, t.name.to_lowercase().contains("court") || t.name.to_lowercase().contains("big"), t.width as u64 * t.height as u64);
    list.sort_by(|a, b| key(b).cmp(&key(a)));
    Ok(list)
}

pub fn inspect_target(path: &Path) -> Result<Inspection> {
    let archive = Archive::open(path, MAX_BASE_ARCHIVE_BYTES)?;
    let list = textures(&archive, true)?;
    let selected = list[0].name.clone();
    Ok(Inspection { path: path.to_string_lossy().into_owned(), textures: list, selected, source_bounds: None, source_revision: None, alignment_source: None })
}

/// The layout record embedded in game-UV exports (`CourtCreator-Layout-v1`).
pub fn layout_comment(texture: &str, bounds: [i64; 4]) -> Vec<u8> {
    let record = json!({"mapping": "game-uv", "texture": texture, "size": [8192, 4096], "courtBounds": bounds});
    let mut comment = LAYOUT_PREFIX.to_vec();
    comment.extend_from_slice(crate::pyjson::dumps(&record, crate::pyjson::Style::COMPACT).as_bytes());
    comment
}

pub fn recorded_layout(comment: &[u8], texture: &Texture) -> Option<[i64; 4]> {
    if !comment.starts_with(LAYOUT_PREFIX) || comment.len() > 4096 {
        return None;
    }
    let layout: Value = serde_json::from_slice(&comment[LAYOUT_PREFIX.len()..]).ok()?;
    let bounds = layout.get("courtBounds")?.as_array()?;
    if layout.get("mapping")?.as_str()? != "game-uv" || layout.get("texture")?.as_str()? != texture.name || layout.get("size")? != &json!([texture.width, texture.height]) || bounds.len() != 4 || bounds.iter().any(|b| !b.is_i64()) {
        return None;
    }
    let b: Vec<i64> = bounds.iter().map(|v| v.as_i64().unwrap()).collect();
    (0 <= b[0] && b[0] < b[2] && b[2] <= texture.width as i64 && 0 <= b[1] && b[1] < b[3] && b[3] <= texture.height as i64).then(|| [b[0], b[1], b[2], b[3]])
}

// --- Regular export base cache ---

pub fn base_path() -> PathBuf {
    files::data_root().join("cache").join("nba2k27-full-court-base.iff")
}

/// A complete cached base: version comment plus a replaceable 8192 x 4096 texture.
pub fn cached_base(path: &Path) -> Option<Inspection> {
    let archive = Archive::open(path, MAX_BASE_ARCHIVE_BYTES).ok()?;
    if archive.comment != BASE_VERSION {
        return None;
    }
    let list = textures(&archive, true).ok()?;
    let selected = list[0].name.clone();
    Some(Inspection { path: path.to_string_lossy().into_owned(), textures: list, selected, source_bounds: None, source_revision: None, alignment_source: None })
}

/// Full archive validation used before adopting an existing (read-only) base from another install.
pub fn validate_base(path: &Path) -> Result<Inspection> {
    let archive = Archive::open(path, MAX_BASE_ARCHIVE_BYTES)?;
    if archive.comment != BASE_VERSION {
        return Err(Error::msg("The export base is not a regular CourtCreator-FullCourt-v3 base."));
    }
    if archive.total_size() > MAX_BASE_ARCHIVE_BYTES {
        return Err(Error::msg("The export base exceeds its inflated archive size limit."));
    }
    archive.test()?;
    let scene = Scene::parse(&archive.read(SCENE_ENTRY, scene::MAX_SCENE_BYTES as u64)?)?;
    if !scene.has_floor_surface() {
        return Err(Error::msg("The export base has no recognized full-court floor surface."));
    }
    // Every geometry reference must be a decoded raw buffer of the declared length.
    for model in scene.models() {
        let mut descriptors: Vec<&Value> = model.get("VertexStream").and_then(Value::as_array).map(|s| s.iter().collect()).unwrap_or_default();
        descriptors.extend(model.get("IndexBuffer").filter(|v| v.is_object()));
        for descriptor in descriptors {
            let name = descriptor.get("Binary").and_then(Value::as_str).unwrap_or("");
            if name.ends_with(".gz") || descriptor.get("CompressionMethod").is_some() {
                return Err(Error::msg("The export base still references compressed geometry."));
            }
            let size = descriptor.get("Size").and_then(Value::as_u64).unwrap_or(0);
            let data = archive.read(name, floor_geometry::MAX_BUFFER_BYTES)?;
            if data.len() as u64 != size || data.starts_with(&[0x1f, 0x8b, 0x21]) {
                return Err(Error::msg("The export base contains undecoded or truncated geometry."));
            }
        }
    }
    let list = textures(&archive, true)?;
    let selected = list[0].name.clone();
    Ok(Inspection { path: path.to_string_lossy().into_owned(), textures: list, selected, source_bounds: None, source_revision: None, alignment_source: None })
}

/// Cleans an extracted stock floor into the regular base (line meshes removed, buffers decoded,
/// arena 000 UV1 lightmap mapping) and writes it to `destination`.
pub fn clean_base_archive(source: &Path, destination: &Path, game_root: Option<&Path>, cancelled: &dyn Fn() -> bool) -> Result<Inspection> {
    let original = Archive::open(source, MAX_BASE_ARCHIVE_BYTES)?;
    if original.total_size() > MAX_BASE_ARCHIVE_BYTES {
        return Err(Error::msg("The stock export base exceeds its inflated archive size limit."));
    }
    let cleaned = scene::clean_floor_scene(&original.read(SCENE_ENTRY, scene::MAX_SCENE_BYTES as u64)?)?;
    let mut replacements = floor_geometry::normalized_scene(&original, &cleaned, game_root)?;
    check(cancelled)?;
    floor_geometry::regular_lightmap(&mut replacements, game_root, cancelled)?;
    check(cancelled)?;
    let mut inspection = None;
    files::write_atomic_checked(
        destination,
        &[source],
        |file| {
            let mut writer = std::io::BufWriter::new(file);
            ziparc::rewrite(&original, &replacements, BASE_VERSION, &mut writer)?;
            use std::io::Write;
            writer.flush()?;
            Ok(())
        },
        |staged| {
            let checked = Archive::open(staged, MAX_BASE_ARCHIVE_BYTES)?;
            if checked.total_size() > MAX_BASE_ARCHIVE_BYTES {
                return Err(Error::msg("The prepared export base exceeds its archive size limit."));
            }
            checked.test()?;
            for (name, data) in &replacements {
                if !ziparc::matches(&checked, name, data) {
                    return Err(Error::msg("The prepared export base failed validation."));
                }
            }
            inspection = Some(inspect_target(staged)?);
            Ok(())
        },
    )?;
    let mut inspection = inspection.unwrap();
    inspection.path = destination.to_string_lossy().into_owned();
    Ok(inspection)
}

/// Port of `prepare_2k27_base`: reuse a valid cache, adopt a validated existing base, or extract
/// the stock MyTEAM floor from the installed game and clean it.
pub fn prepare_base(game_root: Option<&Path>, cancelled: &dyn Fn() -> bool) -> Result<Inspection> {
    let destination = base_path();
    let _lock = crate::lock::FileLock::acquire(&destination.with_extension("iff.lock"), std::time::Duration::from_secs(240))?;
    if let Some(cached) = cached_base(&destination) {
        if let Some(root) = game_root {
            crate::game::remember_root(&crate::game::find_root(Some(root))?)?;
        }
        return Ok(cached);
    }
    if game_root.is_none() {
        for legacy in crate::legacy::roots() {
            let candidate = legacy.join("data").join("generated").join("nba2k27-full-court-base.iff");
            if candidate.is_file() && validate_base(&candidate).is_ok() {
                let bytes = files::read_bounded(&candidate, MAX_BASE_ARCHIVE_BYTES)?;
                files::write_atomic(&destination, &bytes, &[&candidate])?;
                if let Some(result) = cached_base(&destination) {
                    return Ok(result);
                }
            }
        }
    }
    let root = crate::game::find_root(game_root)?;
    let extracted = crate::game::extract_stock_floor(&root, BASE_ENTRY, cancelled)?;
    let result = clean_base_archive(extracted.path(), &destination, Some(&root), cancelled)?;
    let _ = crate::game::remember_root(&root);
    Ok(result)
}

// --- Export packaging ---

/// DDS profile of the base's selected court texture; exports reuse its exact header fields.
pub fn base_texture_profile(archive: &Archive, name: &str) -> Result<canvas_engine::dds::Metadata> {
    let header = archive.read_prefix(name, 148)?;
    let mut profile = canvas_engine::dds::Metadata::inspect(&header)?;
    profile.file_size = 0;
    Ok(profile)
}

/// Writes an IFF from the base: the court DDS and (optionally) the floor scene are replaced; every
/// other member keeps its exact archived bytes. The layout comment records the court bounds.
pub fn package_texture(base: &Archive, texture: &Texture, dds: &[u8], scene_override: Option<&[u8]>, comment: &[u8], output: &Path, sources: &[&Path]) -> Result<()> {
    let header = dds_header_info(&dds[..dds.len().min(148)], dds.len() as u64, &texture.name, true).ok_or_else(|| Error::msg("The DDS encoder did not produce a complete, valid single-surface 2D texture."))?;
    if (header.width, header.height) != (8192, 4096) || header.format != texture.format || header.mipmaps != texture.mipmaps.max(1) {
        return Err(Error::msg("The converted DDS did not retain the base texture's size, format, and mipmaps."));
    }
    let mut replacements: HashMap<String, Vec<u8>> = HashMap::new();
    replacements.insert(texture.name.clone(), dds.to_vec());
    if let Some(scene_bytes) = scene_override {
        if !Scene::parse(scene_bytes)?.has_floor_surface() {
            return Err(Error::msg("The replacement floor scene has no recognized full-court floor surface."));
        }
        if !base.contains(SCENE_ENTRY) {
            return Err(Error::msg("The export base has no floor scene to update."));
        }
        replacements.insert(SCENE_ENTRY.into(), scene_bytes.to_vec());
    }
    let inflated: u64 = base.entries.iter().map(|e| replacements.get(&e.name).map(|r| r.len() as u64).unwrap_or(e.size)).sum();
    if inflated > MAX_BASE_ARCHIVE_BYTES {
        return Err(Error::msg("The converted IFF exceeds its inflated archive size limit."));
    }
    files::write_atomic_checked(
        output,
        sources,
        |file| {
            let mut writer = std::io::BufWriter::new(file);
            ziparc::rewrite(base, &replacements, comment, &mut writer)?;
            use std::io::Write;
            writer.flush()?;
            Ok(())
        },
        |staged| {
            let check = Archive::open(staged, MAX_BASE_ARCHIVE_BYTES)?;
            if check.total_size() > MAX_BASE_ARCHIVE_BYTES {
                return Err(Error::msg("The converted IFF exceeds its inflated archive size limit."));
            }
            check.test()?;
            for (name, data) in &replacements {
                if !ziparc::matches(&check, name, data) {
                    return Err(Error::msg("The converted IFF did not pass archive verification."));
                }
            }
            Ok(())
        },
    )
}

/// Encodes 8192 x 4096 straight RGBA with the base texture's DDS profile (format, mips, alpha).
pub fn encode_texture(rgba: &[u8], profile: &canvas_engine::dds::Metadata, cancelled: &dyn Fn() -> bool) -> Result<Vec<u8>> {
    let options = canvas_engine::dds::mip::Options::default();
    Ok(canvas_engine::dds::encode(rgba, profile, options, false, &|| cancelled())?)
}

/// Integer court bounds for the layout record, from the geometry's court-corner guides.
pub fn target_court_bounds(geometry: &Geometry) -> Result<[i64; 4]> {
    geometry.court_corner_bounds()
}
