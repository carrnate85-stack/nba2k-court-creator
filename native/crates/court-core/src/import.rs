//! "Import older court": inspect a legacy court IFF, detect (or read the recorded) court edges of its
//! artwork and align that artwork onto a modern 2K27 hardwood texture (ports of
//! `court_import.inspect_iff`, `detect_court_edges`, `_aligned_image`, `render_texture`, `build_iff`
//! and `backend.preview_import` / `export_import_png` / `export_import_iff`).
use crate::error::{Error, Result, check};
use crate::files;
use crate::geometry::Geometry;
use crate::iff::{self, Inspection, Texture};
use crate::raster::{self, Filter, Rgba};
use crate::scene::{self, SCENE_ENTRY};
use crate::ziparc::Archive;
use rayon::prelude::*;
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

pub const OUTPUT_SIZE: (u32, u32) = (8192, 4096);
pub const PREVIEW_SIZE: (u32, u32) = (1200, 600);
/// Whole source archives are read into memory; Python streamed them without an archive-size cap.
pub const MAX_SOURCE_ARCHIVE_BYTES: u64 = 1024 * 1024 * 1024;
/// Court guide drawn on previews (`ImageDraw.rectangle(outline="#12A88B")`).
const GUIDE_COLOR: [u8; 3] = [0x12, 0xA8, 0x8B];
const DDS_CACHE_BYTES: usize = 128 * 1024 * 1024;
const DDS_CACHE_LIMIT: usize = 2;

/// One conversion request (the request JSON fields read by `backend._import_arguments`).
#[derive(Clone, Debug)]
pub struct ImportRequest {
    pub source_path: PathBuf,
    pub texture_name: String,
    /// Court edges in source-texture pixels: left, top, right, bottom.
    pub bounds: [i64; 4],
    pub background_path: PathBuf,
    pub background_revision: Option<String>,
    pub source_revision: Option<String>,
}

#[inline]
fn round(value: f64) -> i64 {
    // Python's round(): ties to even.
    value.round_ties_even() as i64
}

fn source_archive(path: &Path) -> Result<Archive> {
    if !path.is_file() {
        return Err(Error::msg("Choose a ZIP-style NBA 2K court .iff file."));
    }
    // Archive::parse enforces the 8 MB directory, 20000-entry and duplicate-name rules.
    Archive::open(path, MAX_SOURCE_ARCHIVE_BYTES)
}

/// Port of `_read_dds_data`: the complete DDS member, its validated header and the source revision
/// (sha256 of archive comment, NUL, UTF-8 name, NUL, DDS bytes).
fn read_dds_data(archive: &Archive, name: &str, expected_revision: Option<&str>) -> Result<(Vec<u8>, Texture, String)> {
    if !archive.contains(name) {
        return Err(Error::msg("The selected DDS texture is no longer in this IFF. Reimport the source court."));
    }
    let descriptor = iff::dds_info(archive, name).ok_or_else(|| Error::msg("The selected DDS has an invalid, oversized or unsupported 2D texture header."))?;
    let data = archive.read(name, iff::MAX_DDS_BYTES)?;
    if data.len() as u64 != descriptor.bytes || data.len() as u64 > iff::MAX_DDS_BYTES {
        return Err(Error::msg("The selected DDS texture is incomplete or exceeds the size limit."));
    }
    let mut digest = Sha256::new();
    digest.update(&archive.comment);
    digest.update(b"\0");
    digest.update(name.as_bytes());
    digest.update(b"\0");
    digest.update(&data);
    let revision = files::hex(&digest.finalize());
    if let Some(expected) = expected_revision {
        if !files::is_revision(expected) {
            return Err(Error::msg("Invalid source court revision."));
        }
        if expected != revision {
            return Err(Error::msg("The source court changed after its preview was prepared. Reimport the source court before converting."));
        }
    }
    Ok((data, descriptor, revision))
}

fn u32le(data: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(data[offset..offset + 4].try_into().unwrap())
}

/// Uncompressed 8-bit-per-channel DDS layouts Pillow also reads (the shared decoder covers BCn only).
fn decode_uncompressed(data: &[u8], descriptor: &Texture) -> Option<Rgba> {
    let (width, height) = (descriptor.width as usize, descriptor.height as usize);
    let fourcc = &data[84..88];
    // Byte offsets of R, G, B and (optional) A within each pixel.
    let (header, bytes_per_pixel, offsets): (usize, usize, [Option<usize>; 4]) = if fourcc == b"DX10" {
        match u32le(data, 128) {
            27..=29 => (148, 4, [Some(0), Some(1), Some(2), Some(3)]),
            87 | 90 | 91 => (148, 4, [Some(2), Some(1), Some(0), Some(3)]),
            _ => return None,
        }
    } else {
        let flags = u32le(data, 80);
        let bit_count = u32le(data, 88);
        if flags & 0x40 == 0 || flags & 0x4 != 0 || !(bit_count == 24 || bit_count == 32) {
            return None;
        }
        let bytes = bit_count as usize / 8;
        let lane = |mask: u32| -> Option<Option<usize>> {
            (0..bytes).find(|&i| mask == 0xFF << (8 * i)).map(Some)
        };
        let alpha = if flags & 0x1 != 0 { lane(u32le(data, 104))? } else { None };
        (128, bytes, [lane(u32le(data, 92))?, lane(u32le(data, 96))?, lane(u32le(data, 100))?, alpha])
    };
    let payload = data.get(header..header + width * height * bytes_per_pixel)?;
    let mut out = vec![255u8; width * height * 4];
    out.par_chunks_exact_mut(4).zip(payload.par_chunks_exact(bytes_per_pixel)).for_each(|(pixel, source)| {
        for (c, offset) in offsets.iter().enumerate() {
            if let Some(offset) = offset {
                pixel[c] = source[*offset];
            }
        }
    });
    Rgba::from_raw(width as u32, height as u32, out).ok()
}

/// Port of `_decode_dds`: straight RGBA pixels of the validated DDS.
fn decode_dds(data: &[u8], descriptor: &Texture) -> Result<Rgba> {
    let image = match raster::decode_dds(data) {
        Ok(image) => image,
        Err(_) => decode_uncompressed(data, descriptor).ok_or_else(|| Error::msg("The selected DDS is damaged or its compression is not supported by the image decoder."))?,
    };
    if (image.width, image.height) != (descriptor.width, descriptor.height) {
        return Err(Error::msg("The DDS decoder returned dimensions that do not match its validated header."));
    }
    Ok(image)
}

static DDS_CACHE: Mutex<Vec<(String, Arc<Rgba>)>> = Mutex::new(Vec::new());

/// Port of `_import_pixels`' decoded-image LRU cache (two images, 128 MiB), keyed by source revision.
fn decoded(data: &[u8], descriptor: &Texture, revision: &str) -> Result<Arc<Rgba>> {
    {
        let mut cache = DDS_CACHE.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(index) = cache.iter().position(|(key, _)| key == revision) {
            let item = cache.remove(index);
            let image = item.1.clone();
            cache.push(item);
            return Ok(image);
        }
    }
    let image = Arc::new(decode_dds(data, descriptor)?);
    let size = image.data.len();
    if size <= DDS_CACHE_BYTES {
        let mut cache = DDS_CACHE.lock().unwrap_or_else(|e| e.into_inner());
        if !cache.iter().any(|(key, _)| key == revision) {
            let mut used: usize = cache.iter().map(|(_, i)| i.data.len()).sum();
            while !cache.is_empty() && (cache.len() >= DDS_CACHE_LIMIT || used + size > DDS_CACHE_BYTES) {
                used -= cache.remove(0).1.data.len();
            }
            cache.push((revision.to_string(), image.clone()));
        }
    }
    Ok(image)
}

/// Decoded source texture for a request, verifying its recorded revision.
fn source_pixels(request: &ImportRequest) -> Result<Arc<Rgba>> {
    let archive = source_archive(&request.source_path)?;
    let (data, descriptor, revision) = read_dds_data(&archive, &request.texture_name, request.source_revision.as_deref())?;
    drop(archive);
    decoded(&data, &descriptor, &revision)
}

/// Port of `inspect_iff(path, selected=...)` in source (non-target) mode: lists the DDS textures,
/// picks the selected (or best) one and reports its court edges, from the recorded
/// `CourtCreator-Layout-v1` comment when present, otherwise from image detection.
pub fn inspect(path: &Path, selected: Option<&str>) -> Result<Inspection> {
    let archive = source_archive(path)?;
    let textures = iff::textures(&archive, false)?;
    let chosen = match selected.filter(|name| !name.is_empty()) {
        Some(name) => textures.iter().find(|t| t.name == name).cloned().ok_or_else(|| Error::msg("The selected texture is no longer available. Reimport the source court."))?,
        None => textures[0].clone(),
    };
    let recorded = iff::recorded_layout(&archive.comment, &chosen);
    let (data, descriptor, revision) = read_dds_data(&archive, &chosen.name, None)?;
    if descriptor != chosen {
        return Err(Error::msg("The source court changed during inspection. Reimport it."));
    }
    drop(archive);
    let bounds = match recorded {
        Some(bounds) => bounds,
        None => detect_court_edges(&*decoded(&data, &descriptor, &revision)?),
    };
    Ok(Inspection {
        path: path.to_string_lossy().into_owned(),
        selected: chosen.name.clone(),
        textures,
        source_bounds: Some(bounds),
        source_revision: Some(revision),
        alignment_source: Some(if recorded.is_some() { "recorded-game-uv" } else { "image-detection" }.into()),
    })
}

// --- Court edge detection ---

/// Pillow RGB image (the padding byte is unused).
struct Rgb {
    width: usize,
    height: usize,
    data: Vec<[u8; 3]>,
}

impl Rgb {
    #[inline]
    fn at(&self, x: usize, y: usize) -> [u8; 3] {
        self.data[y * self.width + x]
    }
    fn transpose(&self) -> Rgb {
        let mut data = vec![[0u8; 3]; self.data.len()];
        for y in 0..self.height {
            for x in 0..self.width {
                data[x * self.height + y] = self.data[y * self.width + x];
            }
        }
        Rgb { width: self.height, height: self.width, data }
    }
}

/// `_gaussian_blur_radius` (BoxBlur.c), in the same single-precision arithmetic.
fn gaussian_box_radius(radius: f32, passes: i32) -> f32 {
    let sigma2 = radius * radius / passes as f32;
    let length = (12.0 * sigma2 as f64 + 1.0).sqrt() as f32;
    let l = ((length as f64 - 1.0) / 2.0).floor() as f32;
    let mut a = (2.0 * l + 1.0) * (l * (l + 1.0) - 3.0 * sigma2);
    a /= 6.0 * (sigma2 - (l + 1.0) * (l + 1.0));
    l + a
}

/// `ImagingLineBoxBlur32` for one line with a fractional box radius.
fn line_box_blur(input: &[[u8; 3]], output: &mut [[u8; 3]], radius: i32, edge_a: i32, edge_b: i32, ww: u32, fw: u32) {
    let last = input.len() as i32 - 1;
    let p = |i: i32| input[i as usize];
    let mut acc = [0u32; 3];
    for c in 0..3 {
        acc[c] = (p(0)[c] as u32).wrapping_mul((radius + 1) as u32);
        for x in 0..edge_a - 1 {
            acc[c] = acc[c].wrapping_add(p(x)[c] as u32);
        }
        acc[c] = acc[c].wrapping_add((p(last)[c] as u32).wrapping_mul((radius - edge_a + 1) as u32));
    }
    let mut step = |x: i32, subtract: i32, add: i32, left: i32, right: i32, acc: &mut [u32; 3]| {
        for c in 0..3 {
            acc[c] = acc[c].wrapping_add((p(add)[c] as i32 - p(subtract)[c] as i32) as u32);
            let bulk = acc[c].wrapping_mul(ww).wrapping_add((p(left)[c] as u32 + p(right)[c] as u32).wrapping_mul(fw));
            output[x as usize][c] = (bulk.wrapping_add(1 << 23) >> 24) as u8;
        }
    };
    if edge_a <= edge_b {
        for x in 0..edge_a {
            step(x, 0, x + radius, 0, x + radius + 1, &mut acc);
        }
        for x in edge_a..edge_b {
            step(x, x - radius - 1, x + radius, x - radius - 1, x + radius + 1, &mut acc);
        }
        for x in edge_b..=last {
            step(x, x - radius - 1, last, x - radius - 1, last, &mut acc);
        }
    } else {
        for x in 0..edge_b {
            step(x, 0, x + radius, 0, x + radius + 1, &mut acc);
        }
        for x in edge_b..edge_a {
            step(x, 0, last, 0, last, &mut acc);
        }
        for x in edge_a..=last {
            step(x, x - radius - 1, last, x - radius - 1, last, &mut acc);
        }
    }
}

/// `ImagingHorizontalBoxBlur` applied `passes` times to every row.
fn horizontal_box_blur(image: &mut Rgb, radius: f32, passes: i32) {
    let whole = radius as i32;
    let ww = (16_777_216f32 / (radius * 2.0 + 1.0)) as u32;
    let fw = (16_777_216u32.wrapping_sub(((whole * 2 + 1) as u32).wrapping_mul(ww))) / 2;
    let width = image.width as i32;
    let edge_a = (whole + 1).min(width);
    let edge_b = (width - whole - 1).max(0);
    image.data.par_chunks_exact_mut(image.width).for_each(|row| {
        let mut line = vec![[0u8; 3]; row.len()];
        for _ in 0..passes {
            line_box_blur(row, &mut line, whole, edge_a, edge_b, ww, fw);
            row.copy_from_slice(&line);
        }
    });
}

/// `image.filter(ImageFilter.GaussianBlur(radius))` (three box-blur passes per axis).
fn gaussian_blur(image: &Rgb, radius: f32) -> Rgb {
    let box_radius = gaussian_box_radius(radius, 3);
    let mut out = Rgb { width: image.width, height: image.height, data: image.data.clone() };
    if radius == 0.0 {
        return out;
    }
    if box_radius != 0.0 {
        horizontal_box_blur(&mut out, box_radius, 3);
        let mut transposed = out.transpose();
        horizontal_box_blur(&mut transposed, box_radius, 3);
        out = transposed.transpose();
    }
    out
}

/// Port of `_visible_court_edges`: four long, continuous opposing edges in the downsampled image.
fn visible_court_edges(sample: &Rgb) -> Option<[i64; 4]> {
    let (width, height) = (sample.width as i64, sample.height as i64);
    if width < 40 || height < 24 {
        return None;
    }
    let blurred = gaussian_blur(sample, 0.7);
    let strength = |position: i64, start: i64, end: i64, horizontal: bool| -> (f64, f64) {
        let mut supported = 0usize;
        let mut total = 0u64;
        for cross in start..end {
            let (first, last) = if horizontal {
                (blurred.at(cross as usize, (position - 2) as usize), blurred.at(cross as usize, (position + 2) as usize))
            } else {
                (blurred.at((position - 2) as usize, cross as usize), blurred.at((position + 2) as usize, cross as usize))
            };
            let contrast = (0..3).map(|c| (first[c] as i32 - last[c] as i32).unsigned_abs()).max().unwrap();
            supported += (contrast >= 28) as usize;
            total += contrast as u64;
        }
        let count = (end - start) as f64;
        (supported as f64 / count, total as f64 / count)
    };
    let edge = |dimension: i64, cross_dimension: i64, low: f64, high: f64, horizontal: bool, outermost_first: bool| -> Option<i64> {
        let (start, end) = (round(cross_dimension as f64 * 0.2), round(cross_dimension as f64 * 0.8));
        let mut candidates: Vec<f64> = Vec::new();
        let mut run: Vec<(i64, f64)> = Vec::new();
        let flush = |run: &mut Vec<(i64, f64)>, candidates: &mut Vec<f64>| {
            if !run.is_empty() {
                let weighted: f64 = run.iter().map(|&(p, w)| p as f64 * w).sum();
                let weights: f64 = run.iter().map(|&(_, w)| w).sum();
                candidates.push(weighted / weights);
                run.clear();
            }
        };
        let threshold = if horizontal { 0.75 } else { 0.55 };
        for position in 2.max(round(dimension as f64 * low))..(dimension - 2).min(round(dimension as f64 * high)) {
            let (support, contrast) = strength(position, start, end, horizontal);
            if support >= threshold {
                run.push((position, contrast));
            } else {
                flush(&mut run, &mut candidates);
            }
        }
        flush(&mut run, &mut candidates);
        let chosen = if outermost_first { candidates.first() } else { candidates.last() }?;
        Some(round(*chosen))
    };
    // Long opposing edges distinguish the playing surface from a colored apron;
    // logos, arcs, and lettering do not provide four continuous rectangle edges.
    let bounds = [
        edge(width, height, 0.025, 0.4, false, true)?,
        edge(height, width, 0.025, 0.4, true, true)?,
        edge(width, height, 0.6, 0.975, false, false)?,
        edge(height, width, 0.6, 0.975, true, false)?,
    ];
    let [left, top, right, bottom] = bounds;
    let inset = 3;
    if right - left <= 2 * inset || bottom - top <= 2 * inset {
        return None;
    }
    for (position, start, end, horizontal) in [
        (top, left + inset, right - inset, true),
        (bottom, left + inset, right - inset, true),
        (left, top + inset, bottom - inset, false),
        (right, top + inset, bottom - inset, false),
    ] {
        if strength(position, start, end, horizontal).0 < if horizontal { 0.7 } else { 0.55 } {
            return None;
        }
    }
    Some(bounds)
}

/// The ≤768 x 384 RGB detection sample (`image.resize(..., BILINEAR).convert("RGB")`).
fn detection_sample(image: &Rgba) -> Rgb {
    let scale = 1f64.min(768.0 / image.width as f64).min(384.0 / image.height as f64);
    let width = round(image.width as f64 * scale).max(1) as u32;
    let height = round(image.height as f64 * scale).max(1) as u32;
    let resized = raster::resize(image, width, height, Filter::Bilinear, None);
    Rgb { width: width as usize, height: height as usize, data: resized.data.chunks_exact(4).map(|p| [p[0], p[1], p[2]]).collect() }
}

/// Port of `detect_court_edges`: court rectangle in source-texture pixels.
pub fn detect_court_edges(image: &Rgba) -> [i64; 4] {
    let sample = detection_sample(image);
    let (iw, ih) = (image.width as i64, image.height as i64);
    let (width, height) = (sample.width as i64, sample.height as i64);
    let scale_back = |value: i64, original: i64, dimension: i64| round((value * original) as f64 / dimension as f64);
    if let Some(v) = visible_court_edges(&sample) {
        return [scale_back(v[0], iw, width), scale_back(v[1], ih, height), scale_back(v[2], iw, width), scale_back(v[3], ih, height)];
    }
    let occupied: Vec<bool> = sample.data.iter().map(|p| *p.iter().max().unwrap() > 12).collect();
    let (w, h) = (width as usize, height as usize);
    let rows: Vec<usize> = (0..h).filter(|&y| occupied[y * w..(y + 1) * w].iter().filter(|&&o| o).count() as f64 > width as f64 * 0.38).collect();
    let (Some(&top), Some(&bottom)) = (rows.first(), rows.last()) else { return [0, 0, iw, ih] };
    let (top, bottom) = (top as i64, bottom as i64);
    let limit = (bottom - top - 3).max(1) as f64 * 0.7;
    let columns: Vec<i64> = (0..width).filter(|&x| ((top + 2)..(bottom - 1)).filter(|&y| occupied[y as usize * w + x as usize]).count() as f64 > limit).collect();
    let (Some(&first), Some(&last)) = (columns.first(), columns.last()) else { return [0, 0, iw, ih] };
    [scale_back(first, iw, width), scale_back(top, ih, height), scale_back(last + 1, iw, width), scale_back(bottom + 1, ih, height)]
}

// --- Alignment and rendering ---

/// Port of `target_court_bounds(output_size, geometry=...)`: the stock court-corner rectangle
/// scaled from 8192 x 4096 to the output size.
pub fn target_court_bounds(width: u32, height: u32, geometry: &Geometry) -> Result<[i64; 4]> {
    let bounds = geometry.court_corner_bounds()?;
    let scales = [width, height, width, height];
    let originals = [OUTPUT_SIZE.0, OUTPUT_SIZE.1, OUTPUT_SIZE.0, OUTPUT_SIZE.1];
    Ok(std::array::from_fn(|i| round((bounds[i] * scales[i] as i64) as f64 / originals[i] as f64)))
}

/// `ImageDraw.rectangle(box, outline=color, width=width)` on an opaque canvas (ImagingDrawRectangle).
fn draw_rectangle_outline(image: &mut Rgba, bounds: [i64; 4], color: [u8; 3], width: i64) {
    let [x0, mut y0, x1, mut y1] = bounds;
    if y0 > y1 {
        std::mem::swap(&mut y0, &mut y1);
    }
    let (w, h) = (image.width as i64, image.height as i64);
    let mut set = |x: i64, y: i64| {
        if (0..w).contains(&x) && (0..h).contains(&y) {
            let i = ((y * w + x) * 4) as usize;
            image.data[i..i + 3].copy_from_slice(&color);
        }
    };
    for i in 0..width {
        for y in [y0 + i, y1 - i] {
            for x in x0.min(x1)..=x0.max(x1) {
                set(x, y);
            }
        }
        // Vertical sides: `line32` draws |dy| pixels, excluding its end point.
        let (start, end) = (y0 + width, y1 - width + 1);
        let step = if end >= start { 1 } else { -1 };
        for x in [x1 - i, x0 + i] {
            let mut y = start;
            for _ in 0..(end - start).abs() {
                set(x, y);
                y += step;
            }
        }
    }
}

/// Opaque background: `Image.open(...).convert("RGB").resize(size, BILINEAR)`.
fn background_image(path: &Path, revision: Option<&str>, width: u32, height: u32) -> Result<Rgba> {
    let mut image = raster::decode_file(path, revision)?;
    image.set_alpha(255);
    Ok(raster::resample(&image, width, height, Filter::Bilinear, [0.0, 0.0, image.width as f64, image.height as f64]))
}

/// Port of `_aligned_image`: maps the source court edges onto the stock court rectangle and pastes
/// the warped artwork over the resized hardwood using its alpha. The result is opaque.
fn aligned_image(source: &Rgba, bounds: [i64; 4], background: &Path, background_revision: Option<&str>, size: (u32, u32), guide: bool, geometry: &Geometry) -> Result<Rgba> {
    let [left, top, right, bottom] = bounds;
    if !(0 <= left && left < right && right <= source.width as i64 && 0 <= top && top < bottom && bottom <= source.height as i64) {
        return Err(Error::msg("Court edges must stay inside the imported texture."));
    }
    let (width, height) = size;
    let target = target_court_bounds(width, height, geometry)?;
    let [tx1, ty1, tx2, ty2] = target;
    let scale_x = (right - left) as f64 / (tx2 - tx1) as f64;
    let scale_y = (bottom - top) as f64 / (ty2 - ty1) as f64;
    let matrix = [scale_x, 0.0, left as f64 - tx1 as f64 * scale_x, 0.0, scale_y, top as f64 - ty1 as f64 * scale_y];
    let mut base = background_image(background, background_revision, width, height)?;
    let overlay = raster::affine_bicubic(source, width, height, matrix, true);
    // `base.paste(overlay, (0, 0), overlay.getchannel("A"))`: Pillow BLEND per RGB byte.
    base.data.par_chunks_exact_mut(4).zip(overlay.data.par_chunks_exact(4)).for_each(|(out, pixel)| {
        let mask = pixel[3] as u32;
        if mask == 255 {
            out[..3].copy_from_slice(&pixel[..3]);
        } else if mask != 0 {
            for c in 0..3 {
                let tmp = out[c] as u32 * (255 - mask) + pixel[c] as u32 * mask + 128;
                out[c] = (((tmp >> 8) + tmp) >> 8) as u8;
            }
        }
    });
    if guide {
        draw_rectangle_outline(&mut base, target, GUIDE_COLOR, (width as i64 / 500).max(2));
    }
    Ok(base)
}

fn check_request(request: &ImportRequest) -> Result<()> {
    if let Some(revision) = &request.source_revision {
        if !files::is_revision(revision) {
            return Err(Error::msg("Invalid source court revision."));
        }
    }
    if let Some(revision) = &request.background_revision {
        if !files::is_revision(revision) {
            return Err(Error::msg("Invalid artwork source revision."));
        }
    }
    if !request.background_path.is_file() {
        return Err(Error::msg("The selected 2K27 hardwood texture is missing."));
    }
    Ok(())
}

fn render(request: &ImportRequest, geometry: &Geometry, size: (u32, u32), guide: bool) -> Result<Rgba> {
    check_request(request)?;
    let source = source_pixels(request)?;
    aligned_image(&source, request.bounds, &request.background_path, request.background_revision.as_deref(), size, guide, geometry)
}

/// Port of `render_texture(..., preview=True)`: 1200 x 600 alignment preview with the court guide.
pub fn preview(request: &ImportRequest, geometry: &Geometry) -> Result<Rgba> {
    render(request, geometry, PREVIEW_SIZE, true)
}

/// Port of `render_texture(...)`: the converted 8192 x 4096 court texture (opaque).
pub fn render_full(request: &ImportRequest, geometry: &Geometry) -> Result<Rgba> {
    render(request, geometry, OUTPUT_SIZE, false)
}

/// RGB PNG, like Pillow saving the converted "RGB" image.
pub fn encode_png_rgb(image: &Rgba) -> Result<Vec<u8>> {
    use image::ImageEncoder;
    use image::codecs::png::{CompressionType, FilterType, PngEncoder};
    let rgb: Vec<u8> = image.data.chunks_exact(4).flat_map(|p| [p[0], p[1], p[2]]).collect();
    let mut out = Vec::new();
    PngEncoder::new_with_quality(&mut out, CompressionType::Default, FilterType::Adaptive).write_image(&rgb, image.width, image.height, image::ExtendedColorType::Rgb8)?;
    Ok(out)
}

/// Port of `backend.export_import_png`: renders the converted texture and publishes it atomically.
pub fn export_png(request: &ImportRequest, geometry: &Geometry, output: &Path) -> Result<()> {
    let sources = [request.source_path.as_path(), request.background_path.as_path()];
    files::ensure_new_export(output, &sources)?;
    let image = render_full(request, geometry)?;
    let png = encode_png_rgb(&image)?;
    drop(image);
    files::write_atomic(output, &png, &sources)
}

/// Port of `backend.export_import_iff`: the converted texture packaged into the regular base with
/// the native-export scene and the game-UV layout comment.
pub fn export_iff(request: &ImportRequest, geometry: &Geometry, output: &Path, game_root: Option<&Path>, cancelled: &dyn Fn() -> bool) -> Result<()> {
    let base_path = iff::base_path();
    let sources = [request.source_path.as_path(), request.background_path.as_path(), base_path.as_path()];
    files::ensure_new_export(output, &sources)?;
    check_request(request)?;
    let base = iff::prepare_base(game_root, cancelled)?;
    let archive = Archive::open(&base_path, iff::MAX_BASE_ARCHIVE_BYTES)?;
    let texture = base.textures.iter().find(|t| t.name == base.selected).cloned().ok_or_else(|| Error::msg("The selected target DDS is not a supported full-court texture."))?;
    if !archive.contains(&texture.name) {
        return Err(Error::msg("The selected target DDS is not a supported full-court texture."));
    }
    let scene_override = scene::native_export_scene(&archive.read(SCENE_ENTRY, scene::MAX_SCENE_BYTES as u64)?)?;
    check(cancelled)?;
    let image = render_full(request, geometry)?;
    check(cancelled)?;
    let profile = iff::base_texture_profile(&archive, &texture.name)?;
    let dds = iff::encode_texture(&image.data, &profile, cancelled)?;
    drop(image);
    check(cancelled)?;
    let comment = iff::layout_comment(&texture.name, target_court_bounds(OUTPUT_SIZE.0, OUTPUT_SIZE.1, geometry)?);
    iff::package_texture(&archive, &texture, &dds, Some(&scene_override), &comment, output, &sources)
}

/// Detection internals for parity checks: the RGB sample and its Gaussian blur as opaque RGBA.
#[doc(hidden)]
pub fn detection_debug(image: &Rgba) -> (Rgba, Rgba) {
    let sample = detection_sample(image);
    let blurred = gaussian_blur(&sample, 0.7);
    let pack = |rgb: &Rgb| Rgba { width: rgb.width as u32, height: rgb.height as u32, data: rgb.data.iter().flat_map(|p| [p[0], p[1], p[2], 255]).collect() };
    (pack(&sample), pack(&blurred))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rgb(width: usize, height: usize, f: impl Fn(usize, usize) -> [u8; 3]) -> Rgb {
        Rgb { width, height, data: (0..height).flat_map(|y| (0..width).map(move |x| (x, y))).map(|(x, y)| f(x, y)).collect() }
    }

    #[test]
    fn gaussian_radius_matches_pillow() {
        // Pillow: sigma2 = .49 / 3, l = 0, a = -.49 / (6 * (sigma2 - 1)).
        let r = gaussian_box_radius(0.7, 3);
        assert!((r - 0.097_609_56).abs() < 1e-6, "{r}");
        assert_eq!(gaussian_box_radius(3.0, 3).floor(), 2.0);
    }

    #[test]
    fn blur_keeps_flat_images_and_spreads_edges() {
        let flat = rgb(10, 6, |_, _| [90, 120, 200]);
        assert!(gaussian_blur(&flat, 0.7).data.iter().all(|p| *p == [90, 120, 200]));
        let step = rgb(10, 3, |x, _| if x < 5 { [0; 3] } else { [255; 3] });
        let blurred = gaussian_blur(&step, 0.7);
        let row: Vec<u8> = (0..10).map(|x| blurred.at(x, 1)[0]).collect();
        assert_eq!(row[0], 0);
        assert_eq!(row[9], 255);
        assert!(row[4] > 0 && row[4] < 128 && row[5] > 128 && row[5] < 255, "{row:?}");
        assert!(row.windows(2).all(|w| w[0] <= w[1]));
    }

    #[test]
    fn detects_rectangle_edges() {
        // Light court on a dark apron: left/top at 100/50, right/bottom at 700/350.
        let mut image = Rgba::new(800, 400, [20, 30, 40, 255]);
        for y in 50..350 {
            for x in 100..700 {
                let i = ((y * 800 + x) * 4) as usize;
                image.data[i..i + 3].copy_from_slice(&[200, 160, 110]);
            }
        }
        let bounds = detect_court_edges(&image);
        for (found, expected) in bounds.iter().zip([100, 50, 700, 350]) {
            assert!((found - expected).abs() <= 2, "{bounds:?}");
        }
    }

    #[test]
    fn transparent_or_tiny_images_fall_back() {
        assert_eq!(detect_court_edges(&Rgba::new(64, 32, [0; 4])), [0, 0, 64, 32]);
        assert!(visible_court_edges(&rgb(30, 20, |_, _| [0; 3])).is_none());
    }

    #[test]
    fn rectangle_outline_is_inclusive_frame() {
        let mut image = Rgba::new(12, 10, [0, 0, 0, 255]);
        draw_rectangle_outline(&mut image, [2, 1, 9, 8], [1, 2, 3], 2);
        let inked = |x: u32, y: u32| image.pixel(x, y)[..3] == [1, 2, 3];
        let count = (0..10).flat_map(|y| (0..12).map(move |x| (x, y))).filter(|&(x, y)| inked(x, y)).count();
        // 8 x 8 inclusive box minus its 4 x 4 interior.
        assert_eq!(count, 64 - 16);
        assert!(inked(2, 1) && inked(9, 8) && inked(3, 2) && !inked(4, 3) && !inked(10, 8));
    }

    #[test]
    fn uncompressed_bgra_decodes() {
        let mut data = vec![0u8; 128 + 16 * 16 * 4];
        data[..4].copy_from_slice(b"DDS ");
        for (offset, value) in [(4, 124u32), (12, 16), (16, 16), (76, 32), (80, 0x41), (88, 32), (92, 0xFF0000), (96, 0xFF00), (100, 0xFF), (104, 0xFF00_0000)] {
            data[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
        }
        data[128..132].copy_from_slice(&[1, 2, 3, 4]);
        let descriptor = iff::dds_header_info(&data, data.len() as u64, "t.dds", false).unwrap();
        let image = decode_dds(&data, &descriptor).unwrap();
        assert_eq!(image.pixel(0, 0), [3, 2, 1, 4]);
    }
}
