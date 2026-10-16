//! Raw floor geometry for exports (port of floor_geometry.py): decode Oodle-wrapped vertex/index
//! members, publish raw `.bin` buffers with `.bin` scene references, and fit the regular
//! arena-000 UV1 lightmap mapping onto the template floor.
use crate::error::{Error, Result};
use crate::scene::{SCENE_ENTRY, Scene};
use crate::ziparc::Archive;
use serde_json::Value;
use std::collections::HashMap;
use std::path::Path;

pub const MAX_BUFFER_BYTES: u64 = 16 * 1024 * 1024;
pub const MAX_GEOMETRY_BYTES: u64 = 64 * 1024 * 1024;
pub const REFERENCE_FLOOR: &str = "levels/arena_000_int_floor.iff";

fn valid_name(name: &str) -> bool {
    !name.is_empty() && !name.contains('/') && !name.contains('\\') && name != "." && name != ".."
}

/// Port of `read_buffer`: returns the published member name and its decoded bytes.
pub fn read_buffer(archive: &Archive, descriptor: &Value, game_root: Option<&Path>) -> Result<(String, Vec<u8>)> {
    let size = descriptor.get("Size").filter(|v| v.is_i64() || v.is_u64()).and_then(Value::as_u64).filter(|s| *s > 0 && *s <= MAX_BUFFER_BYTES).ok_or_else(|| Error::msg("Floor geometry has an invalid buffer size."))?;
    let name = descriptor.get("Binary").and_then(Value::as_str).filter(|n| valid_name(n)).ok_or_else(|| Error::msg("Floor geometry has an invalid buffer name."))?;
    let entry = match name.strip_suffix(".gz") {
        Some(stem) => format!("{stem}.bin"),
        None => name.to_string(),
    };
    let info = archive.entry(&entry).ok_or_else(|| Error::msg(format!("Floor geometry is missing {entry}")))?;
    if info.size > MAX_BUFFER_BYTES {
        return Err(Error::msg("Floor geometry exceeds the buffer size limit."));
    }
    let mut raw = archive.read(&entry, MAX_BUFFER_BYTES)?;
    if raw.starts_with(&[0x1f, 0x8b, 0x21]) {
        if raw.len() < 24 || u32::from_le_bytes(raw[raw.len() - 4..].try_into().unwrap()) as u64 != size {
            return Err(Error::msg("Compressed floor geometry has an invalid length."));
        }
        let root = match game_root {
            Some(root) => root.to_path_buf(),
            None => crate::game::find_root(None)?,
        };
        if !crate::oodle::dll_path(&root).is_file() {
            return Err(Error::msg("Select the NBA 2K27 installation to decode its floor geometry."));
        }
        raw = crate::oodle::decompress(&root, &raw[16..raw.len() - 8], size as usize).map_err(|e| e.context("The installed game could not decode the floor geometry: "))?;
    }
    if raw.len() as u64 != size {
        return Err(Error::msg("Floor geometry has an invalid decoded buffer length."));
    }
    Ok((entry, raw))
}

/// Port of `normalized_scene`: decoded buffers plus the updated scene, keyed by member name.
pub fn normalized_scene(archive: &Archive, scene_bytes: &[u8], game_root: Option<&Path>) -> Result<HashMap<String, Vec<u8>>> {
    let mut scene = Scene::parse(scene_bytes)?;
    let mut replacements: HashMap<String, Vec<u8>> = HashMap::new();
    let mut total = 0u64;
    for value in scene.document.as_object_mut().unwrap().values_mut() {
        let Some(models) = value.get_mut("Model").and_then(Value::as_object_mut) else { continue };
        for model in models.values_mut() {
            let model = model.as_object_mut().ok_or_else(|| Error::msg("The floor scene's Model entries must be objects."))?;
            let stream_count = match model.get("VertexStream") {
                None => 0,
                Some(Value::Array(streams)) => streams.len(),
                Some(_) => return Err(Error::msg("Floor geometry has invalid vertex streams.")),
            };
            let has_index = model.get("IndexBuffer").is_some_and(|v| !v.is_null() && v.as_object().is_none_or(|o| !o.is_empty()));
            let mut targets: Vec<(bool, usize)> = (0..stream_count).map(|i| (true, i)).collect();
            if has_index {
                targets.push((false, 0));
            }
            for (stream, index) in targets {
                let descriptor = if stream { &mut model.get_mut("VertexStream").unwrap()[index] } else { model.get_mut("IndexBuffer").unwrap() };
                let object = descriptor.as_object_mut().ok_or_else(|| Error::msg("Floor geometry has an invalid buffer descriptor."))?;
                let size = object.get("Size").filter(|v| v.is_i64() || v.is_u64()).and_then(Value::as_u64).filter(|s| *s > 0).ok_or_else(|| Error::msg("Floor geometry has an invalid buffer size."))?;
                total += size;
                if total > MAX_GEOMETRY_BYTES {
                    return Err(Error::msg("Floor geometry exceeds the total size limit."));
                }
                if let Some(stride) = object.get("Stride") {
                    let stride = stride.as_u64().filter(|_| stride.is_i64() || stride.is_u64()).filter(|s| (1..=1024).contains(s)).ok_or_else(|| Error::msg("Floor geometry has an invalid vertex stride."))?;
                    if size % stride != 0 {
                        return Err(Error::msg("Floor geometry has an invalid vertex stride."));
                    }
                }
                let (entry, raw) = read_buffer(archive, &Value::Object(object.clone()), game_root)?;
                if let Some(existing) = replacements.get(&entry) {
                    if *existing != raw {
                        return Err(Error::msg("Floor geometry has conflicting buffer descriptors."));
                    }
                }
                replacements.insert(entry.clone(), raw);
                object.insert("Binary".into(), Value::String(entry));
                object.shift_remove("CompressionMethod");
            }
        }
    }
    replacements.insert(SCENE_ENTRY.into(), scene.serialize()?);
    Ok(replacements)
}

/// 3-parameter least squares (Householder QR), returning the solution and numerical rank.
fn least_squares(design: &[[f64; 3]], targets: &[[f64; 2]]) -> Option<([[f64; 2]; 3], usize)> {
    let rows = design.len();
    let mut a: Vec<[f64; 3]> = design.to_vec();
    let mut b: Vec<[f64; 2]> = targets.to_vec();
    let mut rank = 0;
    let scale = a.iter().flat_map(|r| r.iter()).fold(0.0f64, |m, v| m.max(v.abs())).max(1.0);
    for column in 0..3 {
        let norm = (column..rows).map(|r| a[r][column] * a[r][column]).sum::<f64>().sqrt();
        if norm <= scale * 1e-12 * (rows as f64) {
            continue;
        }
        rank += 1;
        let alpha = if a[column][column] > 0.0 { -norm } else { norm };
        let mut v: Vec<f64> = (column..rows).map(|r| a[r][column]).collect();
        v[0] -= alpha;
        let vnorm = v.iter().map(|x| x * x).sum::<f64>();
        if vnorm == 0.0 {
            continue;
        }
        for c in column..3 {
            let dot: f64 = (column..rows).map(|r| v[r - column] * a[r][c]).sum();
            let factor = 2.0 * dot / vnorm;
            for r in column..rows {
                a[r][c] -= factor * v[r - column];
            }
        }
        for c in 0..2 {
            let dot: f64 = (column..rows).map(|r| v[r - column] * b[r][c]).sum();
            let factor = 2.0 * dot / vnorm;
            for r in column..rows {
                b[r][c] -= factor * v[r - column];
            }
        }
    }
    if rank < 3 {
        return Some(([[0.0; 2]; 3], rank));
    }
    let mut x = [[0.0f64; 2]; 3];
    for c in 0..2 {
        for row in (0..3).rev() {
            let mut sum = b[row][c];
            for k in row + 1..3 {
                sum -= a[row][k] * x[k][c];
            }
            x[row][c] = sum / a[row][row];
        }
    }
    Some((x, rank))
}

pub struct Lightmap {
    pub transform: [[f64; 2]; 3],
    pub design: Vec<[f64; 3]>,
    pub scale: [f64; 2],
    pub bias: [f64; 2],
}

/// Port of `lightmap_transform`: planar position-to-UV1 mapping of a 28-byte-stride floor.
pub fn lightmap_transform(model: &Value, raw: &[u8], vertex_indices: Option<&[u16]>) -> Result<Lightmap> {
    let streams = model.get("VertexStream").and_then(Value::as_array);
    let format = model.get("VertexFormat");
    let uv = format.and_then(|f| f.get("TEXCOORD1"));
    let unsupported = || Error::msg("The regular floor has an unsupported lightmap vertex layout.");
    if streams.is_none_or(|s| s.len() != 1 || s[0].get("Stride").and_then(Value::as_i64) != Some(28))
        || format.and_then(|f| f.get("POSITION0")).and_then(|p| p.get("Format")).and_then(Value::as_str) != Some("R32G32B32_FLOAT")
        || uv.and_then(|u| u.get("Format")).and_then(Value::as_str) != Some("R16G16_SNORM")
        || uv.and_then(|u| u.get("ByteOffset")).and_then(Value::as_i64) != Some(16)
        || raw.len() % 28 != 0
        || raw.len() < 84
    {
        return Err(unsupported());
    }
    let uv = uv.unwrap();
    let pair = |key: &str| -> Option<[f64; 2]> {
        let list = uv.get(key)?.as_array()?;
        Some([list.first()?.as_f64()?, list.get(1)?.as_f64()?])
    };
    let (scale, bias) = match (pair("Scale"), pair("Offset")) {
        (Some(s), Some(b)) if s.iter().chain(b.iter()).all(|v| v.is_finite()) && s.iter().all(|v| *v > 0.0) => (s, b),
        _ => return Err(Error::msg("The regular floor has an invalid lightmap transform.")),
    };
    let count = raw.len() / 28;
    let mut design = Vec::with_capacity(count);
    let mut uvs = Vec::with_capacity(count);
    let mut max_y = 0.0f64;
    for i in 0..count {
        let o = i * 28;
        let f = |k: usize| f32::from_le_bytes(raw[o + k..o + k + 4].try_into().unwrap()) as f64;
        let (x, y, z) = (f(0), f(4), f(8));
        if !(x.is_finite() && y.is_finite() && z.is_finite()) {
            return Err(Error::msg("The regular floor must be a finite flat surface."));
        }
        max_y = max_y.max(y.abs());
        let u = i16::from_le_bytes([raw[o + 16], raw[o + 17]]) as f64;
        let v = i16::from_le_bytes([raw[o + 18], raw[o + 19]]) as f64;
        design.push([x, z, 1.0]);
        uvs.push([(u / 32767.0).max(-1.0) * scale[0] + bias[0], (v / 32767.0).max(-1.0) * scale[1] + bias[1]]);
    }
    if max_y > 0.001 {
        return Err(Error::msg("The regular floor must be a finite flat surface."));
    }
    let (fit_design, fit_uvs): (Vec<[f64; 3]>, Vec<[f64; 2]>) = match vertex_indices {
        Some(indices) => {
            let mut ids: Vec<usize> = indices.iter().map(|&i| i as usize).collect();
            ids.sort_unstable();
            ids.dedup();
            if ids.len() < 3 || *ids.last().unwrap() >= count {
                return Err(Error::msg("The regular floor has invalid lightmap surface indices."));
            }
            (ids.iter().map(|&i| design[i]).collect(), ids.iter().map(|&i| uvs[i]).collect())
        }
        None => (design.clone(), uvs.clone()),
    };
    let (transform, rank) = least_squares(&fit_design, &fit_uvs).ok_or_else(|| Error::msg("The regular floor does not have a supported planar lightmap."))?;
    let error = fit_design.iter().zip(&fit_uvs).map(|(d, t)| {
        (0..2).map(|c| (d[0] * transform[0][c] + d[1] * transform[1][c] + d[2] * transform[2][c] - t[c]).abs()).fold(0.0, f64::max)
    }).fold(0.0, f64::max);
    if rank != 3 || !transform.iter().flatten().all(|v| v.is_finite()) || error > 0.00002 {
        return Err(Error::msg("The regular floor does not have a supported planar lightmap."));
    }
    Ok(Lightmap { transform, design, scale, bias })
}

fn bounds(model: &Value, key: &str) -> Option<[f64; 3]> {
    let list = model.get(key)?.as_array()?;
    if list.len() != 3 {
        return None;
    }
    let values = [list[0].as_f64()?, list[1].as_f64()?, list[2].as_f64()?];
    values.iter().all(|v| v.is_finite()).then_some(values)
}

/// Port of `remap_lightmap`: replace only UV1, keeping positions, artwork and detail bytes.
pub fn remap_lightmap(model: &Value, raw: &[u8], reference: &Value, reference_raw: &[u8], reference_indices: Option<&[u16]>) -> Result<Vec<u8>> {
    let target = lightmap_transform(reference, reference_raw, reference_indices)?.transform;
    let source = lightmap_transform(model, raw, None)?;
    for key in ["Min", "Max"] {
        match (bounds(model, key), bounds(reference, key)) {
            (Some(a), Some(b)) if a.iter().zip(&b).all(|(x, y)| (x - y).abs() <= 0.02) => {}
            _ => return Err(Error::msg("The regular stock floor bounds differ from the template.")),
        }
    }
    let mut result = raw.to_vec();
    for (i, d) in source.design.iter().enumerate() {
        let mut packed = [0i16; 2];
        for c in 0..2 {
            let uv = d[0] * target[0][c] + d[1] * target[1][c] + d[2] * target[2][c];
            let encoded = ((uv - source.bias[c]) / source.scale[c] * 32767.0).round_ties_even();
            if !encoded.is_finite() || !(-32768.0..=32768.0).contains(&encoded) {
                return Err(Error::msg("The regular floor lightmap exceeds its coordinate range."));
            }
            packed[c] = encoded.clamp(-32767.0, 32767.0) as i16;
        }
        result[i * 28 + 16..i * 28 + 18].copy_from_slice(&packed[0].to_le_bytes());
        result[i * 28 + 18..i * 28 + 20].copy_from_slice(&packed[1].to_le_bytes());
    }
    Ok(result)
}

/// Port of `regular_lightmap`: fit the MyTEAM carrier's UV1 to stock arena 000's floor surface.
pub fn regular_lightmap(replacements: &mut HashMap<String, Vec<u8>>, game_root: Option<&Path>, cancelled: &dyn Fn() -> bool) -> Result<()> {
    let scene = Scene::parse(&replacements[SCENE_ENTRY])?;
    let targets: Vec<&serde_json::Map<String, Value>> = scene
        .scenes()
        .filter(|s| s.get("Attribute").and_then(|a| a.get("VC_LevelId")).and_then(Value::as_str) == Some("floor_myteam2k26_int"))
        .collect();
    if targets.is_empty() {
        return Ok(());
    }
    let root = crate::game::find_root(game_root)?;
    let reference_path = crate::game::extract_stock_floor(&root, REFERENCE_FLOOR, cancelled)?;
    let reference_archive = Archive::open(reference_path.path(), crate::iff::MAX_BASE_ARCHIVE_BYTES)?;
    let reference_scene = Scene::parse(&reference_archive.read(SCENE_ENTRY, crate::scene::MAX_SCENE_BYTES as u64)?)?;
    let references: Vec<&Value> = reference_scene.models().filter(|m| m.get("VertexStream").and_then(Value::as_array).is_some_and(|s| !s.is_empty())).collect();
    if references.len() != 1 {
        return Err(Error::msg("The standard arena 000 floor model is ambiguous."));
    }
    let reference = references[0];
    let (_, reference_raw) = read_buffer(&reference_archive, &reference["VertexStream"][0], Some(&root))?;
    let descriptor = reference.get("IndexBuffer").cloned().unwrap_or(Value::Null);
    if descriptor.get("Format").and_then(Value::as_str) != Some("R16_UINT") {
        return Err(Error::msg("The regular floor has an unsupported index format."));
    }
    let (_, index_raw) = read_buffer(&reference_archive, &descriptor, Some(&root))?;
    // Arena 000's team line meshes have adjusted positions but older UVs: fit the surface only.
    let mut count = 0usize;
    for primitive in reference.get("Prim").and_then(Value::as_array).into_iter().flatten() {
        if primitive.get("Mesh").and_then(Value::as_str).unwrap_or("").to_lowercase().contains("line") {
            break;
        }
        let size = primitive.get("Count").filter(|v| v.is_i64() || v.is_u64()).and_then(Value::as_i64).filter(|c| *c > 0 && c % 3 == 0).ok_or_else(|| Error::msg("The regular floor has invalid surface primitives."))?;
        count += size as usize;
    }
    if count < 3 || count * 2 > index_raw.len() {
        return Err(Error::msg("The regular floor surface is missing or truncated."));
    }
    let indices: Vec<u16> = index_raw[..count * 2].chunks_exact(2).map(|b| u16::from_le_bytes([b[0], b[1]])).collect();
    let mut changed = 0;
    let mut updates = Vec::new();
    for scene in &targets {
        for model in scene.get("Model").and_then(Value::as_object).into_iter().flat_map(|m| m.values()) {
            if !model.get("Prim").and_then(Value::as_array).into_iter().flatten().any(|p| p.get("Mesh").and_then(Value::as_str).unwrap_or("").contains("full_court_floor")) {
                continue;
            }
            let streams = model.get("VertexStream").and_then(Value::as_array);
            let entry = streams.filter(|s| s.len() == 1).and_then(|s| s[0].get("Binary")).and_then(Value::as_str).filter(|b| replacements.contains_key(*b))
                .ok_or_else(|| Error::msg("The template floor has no decoded vertex buffer."))?;
            updates.push((entry.to_string(), remap_lightmap(model, &replacements[entry], reference, &reference_raw, Some(&indices))?));
            changed += 1;
        }
    }
    if changed != 1 {
        return Err(Error::msg("Could not isolate the regular export floor."));
    }
    for (entry, data) in updates {
        replacements.insert(entry, data);
    }
    Ok(())
}

/// Maximum UV1 deviation of `raw` from arena 000's surface mapping (verification helper).
pub fn lightmap_error(model: &Value, raw: &[u8], reference: &Value, reference_raw: &[u8], indices: &[u16]) -> Result<f64> {
    let target = lightmap_transform(reference, reference_raw, Some(indices))?.transform;
    let current = lightmap_transform(model, raw, None)?;
    let mut error = 0.0f64;
    for (i, d) in current.design.iter().enumerate() {
        for c in 0..2 {
            let packed = i16::from_le_bytes([raw[i * 28 + 16 + c * 2], raw[i * 28 + 17 + c * 2]]) as f64;
            let uv = packed / 32767.0 * current.scale[c] + current.bias[c];
            let expected = d[0] * target[0][c] + d[1] * target[1][c] + d[2] * target[2][c];
            error = error.max((uv - expected).abs());
        }
    }
    Ok(error)
}
